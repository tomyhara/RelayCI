using CiRunner.Core.Data;
using Microsoft.Extensions.Hosting;

namespace CiRunner.Core.Engine;

/// <summary>
/// Single dispatch loop: atomically decides "Executor 空き・当該ジョブ非実行中" per build before starting it
/// (spec §5 F1 "同一ジョブの直列性はディスパッチャの不変条件"). Resource locks (F3a) and queue_policy dedup
/// are out of scope for the M1 slice and are picked up in a later milestone.
/// </summary>
public sealed class BuildDispatcher : BackgroundService
{
    private readonly BuildRepository _buildRepo;
    private readonly JobRepository _jobRepo;
    private readonly BuildRunner _buildRunner;
    private readonly GlobalEventHub _eventHub;
    private readonly RetentionService? _retentionService;
    private readonly int _executorLimit;

    private readonly object _stateLock = new();
    private readonly HashSet<long> _runningJobIds = new();
    private int _activeExecutors;
    private readonly SemaphoreSlim _wake = new(1);

    public BuildDispatcher(BuildRepository buildRepo, JobRepository jobRepo, BuildRunner buildRunner, GlobalEventHub eventHub, int executorLimit = 2, RetentionService? retentionService = null)
    {
        _buildRepo = buildRepo;
        _jobRepo = jobRepo;
        _buildRunner = buildRunner;
        _eventHub = eventHub;
        _retentionService = retentionService;
        _executorLimit = executorLimit;
    }

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
        var queued = _buildRepo.ListQueued();
        foreach (var build in queued)
        {
            var job = _jobRepo.FindById(build.JobId);
            if (job is null || !job.Enabled)
            {
                continue;
            }

            lock (_stateLock)
            {
                if (_activeExecutors >= _executorLimit)
                {
                    break;
                }
                if (_runningJobIds.Contains(build.JobId))
                {
                    continue;
                }
                _activeExecutors++;
                _runningJobIds.Add(build.JobId);
            }

            _buildRepo.UpdateStatus(build.Id, Models.BuildStatus.Running, startedAt: DateTimeOffset.Now.ToString("o"));
            _eventHub.Publish(System.Text.Json.JsonSerializer.Serialize(new { buildId = build.Id, type = "build-started", payload = new { } }));

            _ = RunAndReleaseAsync(job, build, stoppingToken);
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
            lock (_stateLock)
            {
                _activeExecutors--;
                _runningJobIds.Remove(build.JobId);
            }
            Signal();
        }
    }
}
