using System.Diagnostics;
using System.Text.Json;
using CiRunner.Core.Data;
using CiRunner.Core.Models;
using Microsoft.Extensions.Hosting;

namespace CiRunner.Core.Engine;

/// <summary>
/// Polling trigger (spec §5 F1 "補助"): periodic <c>git ls-remote</c> against each polled branch of
/// jobs that declare repo_url + polling_branches, triggering a build (DedupKey = SHA) on change.
/// </summary>
public sealed class PollingService : BackgroundService
{
    private readonly JobRepository _jobRepo;
    private readonly JobTriggerService _triggerService;
    private readonly string _gitExePath;
    private readonly int _intervalSec;
    private readonly SettingsRepository? _settings;
    private readonly Dictionary<string, string> _lastKnownSha = new();

    public PollingService(JobRepository jobRepo, JobTriggerService triggerService, string gitExePath, int intervalSec, SettingsRepository? settings = null)
    {
        _jobRepo = jobRepo;
        _triggerService = triggerService;
        _gitExePath = gitExePath;
        _intervalSec = intervalSec;
        _settings = settings;
    }

    /// <summary>Read live each loop iteration so a settings change applies without a restart (spec §5 F6).</summary>
    private TimeSpan CurrentInterval => TimeSpan.FromSeconds(Math.Max(5, _settings?.GetInt("pollingIntervalSec", _intervalSec) ?? _intervalSec));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch
            {
                // one bad job/network hiccup shouldn't kill the polling loop.
            }

            try
            {
                await Task.Delay(CurrentInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var jobs = _jobRepo.ListEnabled()
            .Where(j => !string.IsNullOrEmpty(j.RepoUrl) && !string.IsNullOrEmpty(j.PollingBranches));

        foreach (var job in jobs)
        {
            List<string>? branches;
            try
            {
                branches = JsonSerializer.Deserialize<List<string>>(job.PollingBranches!);
            }
            catch (JsonException)
            {
                continue;
            }
            if (branches is null)
            {
                continue;
            }

            foreach (var branch in branches)
            {
                await PollBranchAsync(job, branch, ct);
            }
        }
    }

    private async Task PollBranchAsync(JobRecord job, string branch, CancellationToken ct)
    {
        var sha = await GetRemoteShaAsync(job.RepoUrl!, branch, ct);
        if (sha is null)
        {
            return;
        }

        var key = $"{job.Id}:{branch}";
        var hadPrevious = _lastKnownSha.TryGetValue(key, out var previousSha);
        _lastKnownSha[key] = sha;

        if (!hadPrevious)
        {
            // First observation after startup: record the baseline without firing, so every
            // restart doesn't immediately rebuild everything it watches.
            return;
        }
        if (previousSha == sha)
        {
            return;
        }

        _triggerService.Trigger(job.Name, BuildTrigger.Polling, requestedParameters: null, dedupKey: sha, commitSha: sha, branch: branch);
    }

    private async Task<string?> GetRemoteShaAsync(string repoUrl, string branch, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _gitExePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("ls-remote");
        psi.ArgumentList.Add(repoUrl);
        psi.ArgumentList.Add($"refs/heads/{branch}");

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
            {
                return null;
            }
            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return line?.Split('\t')[0].Trim();
        }
        catch
        {
            return null;
        }
    }
}
