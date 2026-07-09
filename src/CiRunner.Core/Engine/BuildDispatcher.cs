using System.Text.Json;
using CiRunner.Core.Data;
using Microsoft.Extensions.Hosting;

namespace CiRunner.Core.Engine;

/// <summary>
/// Single dispatch loop: atomically decides "Executor 空き・当該ジョブ非実行中・宣言リソース空き" per
/// build before starting it (spec §5 F1 "同一ジョブの直列性はディスパッチャの不変条件", §5 F3a リソース
/// ロック). queue_policy dedup is handled upstream in JobTriggerService.
/// </summary>
public sealed class BuildDispatcher : BackgroundService
{
    private readonly BuildRepository _buildRepo;
    private readonly JobRepository _jobRepo;
    private readonly BuildRunner _buildRunner;
    private readonly GlobalEventHub _eventHub;
    private readonly RetentionService? _retentionService;
    private readonly int _executorLimit;
    private readonly SettingsRepository? _settings;
    private readonly ResourceLockManager _resourceLocks;

    private readonly object _stateLock = new();
    private readonly HashSet<long> _runningJobIds = new();
    private int _activeExecutors;
    private readonly SemaphoreSlim _wake = new(1);

    public BuildDispatcher(BuildRepository buildRepo, JobRepository jobRepo, BuildRunner buildRunner, GlobalEventHub eventHub, int executorLimit = 2, RetentionService? retentionService = null, SettingsRepository? settings = null, ResourceLockManager? resourceLocks = null)
    {
        _buildRepo = buildRepo;
        _jobRepo = jobRepo;
        _buildRunner = buildRunner;
        _eventHub = eventHub;
        _retentionService = retentionService;
        _executorLimit = executorLimit;
        _settings = settings;
        _resourceLocks = resourceLocks ?? new ResourceLockManager();
    }

    /// <summary>
    /// Read live from settings on every dispatch tick when available, so a system-settings change
    /// (spec §5 F6 "再起動不要で即時反映") applies without restarting the runner. Falls back to the
    /// fixed constructor value when no SettingsRepository was supplied (e.g. unit tests).
    /// </summary>
    private int CurrentExecutorLimit => _settings?.GetInt("executors", _executorLimit) ?? _executorLimit;

    /// <summary>Wakes the dispatch loop immediately instead of waiting for the next poll tick.</summary>
    public void Signal()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            DispatchOnce(stoppingToken);
        }
    }

    private void DispatchOnce(CancellationToken stoppingToken)
    {
        // FIFO order (queued_at), but a build blocked on a resource is skipped rather than stopping
        // the loop, so a later build with lighter resource needs can overtake it (spec §5 F3a
        // "後続の単一リソース待ちが空きリソースを追い越し取得することは許す").
        var queued = _buildRepo.ListQueued();
        foreach (var build in queued)
        {
            var job = _jobRepo.FindById(build.JobId);
            if (job is null || !job.Enabled)
            {
                continue;
            }
            var resources = ParseResources(job.Resources);

            lock (_stateLock)
            {
                if (_activeExecutors >= CurrentExecutorLimit)
                {
                    break;
                }
                if (_runningJobIds.Contains(build.JobId))
                {
                    continue;
                }
                if (resources.Count > 0 && !_resourceLocks.TryAcquireAll(build.Id, resources))
                {
                    // All-or-nothing miss: never partially acquired, so nothing to roll back here.
                    if (build.Status != Models.BuildStatus.Waiting)
                    {
                        _buildRepo.UpdateStatus(build.Id, Models.BuildStatus.Waiting);
                    }
                    continue;
                }
                _activeExecutors++;
                _runningJobIds.Add(build.JobId);
            }

            _buildRepo.UpdateStatus(build.Id, Models.BuildStatus.Running, startedAt: DateTimeOffset.Now.ToString("o"));
            _eventHub.Publish(JsonSerializer.Serialize(new { buildId = build.Id, type = "build-started", payload = new { } }));

            _ = RunAndReleaseAsync(job, build, stoppingToken);
        }
    }

    private static List<string> ParseResources(string resourcesJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(resourcesJson) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    private async Task RunAndReleaseAsync(Models.JobRecord job, Models.BuildRecord build, CancellationToken ct)
    {
        try
        {
            await _buildRunner.RunAsync(job, build, ct);
            _retentionService?.Enforce(job);
        }
        finally
        {
            // Always safe to call even if this build never held any resource (spec §5 F3a "解放:
            // ビルド終了...で自動解放").
            _resourceLocks.ReleaseAll(build.Id);
            lock (_stateLock)
            {
                _activeExecutors--;
                _runningJobIds.Remove(build.JobId);
            }
            Signal();
        }
    }
}
