using System.Text.Json;
using CiRunner.Core.Data;
using CiRunner.Core.Models;

namespace CiRunner.Core.Engine;

/// <summary>
/// Single entry point for queuing a build: parameter validation (F1a), DedupKey / queue_policy
/// handling (§5 F1/F3), and waking the dispatcher. Used by the manual-trigger API, the internal
/// Start-CiJob callback, polling, and cron - so every trigger source gets the same semantics.
/// </summary>
public sealed class JobTriggerService
{
    private readonly JobRepository _jobRepo;
    private readonly BuildRepository _buildRepo;
    private readonly BuildDispatcher _dispatcher;

    public JobTriggerService(JobRepository jobRepo, BuildRepository buildRepo, BuildDispatcher dispatcher)
    {
        _jobRepo = jobRepo;
        _buildRepo = buildRepo;
        _dispatcher = dispatcher;
    }

    public sealed record TriggerResult(bool Queued, BuildRecord? Build, string? JobName, string? Reason);

    public TriggerResult Trigger(string jobName, string trigger, IReadOnlyDictionary<string, string>? requestedParameters, string? dedupKey, string? commitSha = null, string? branch = null)
    {
        var job = _jobRepo.FindByName(jobName);
        if (job is null || !job.Enabled)
        {
            return new TriggerResult(false, null, null, "job-not-found-or-disabled");
        }

        var resolved = ParameterResolver.Resolve(job.Parameters, requestedParameters);
        if (!resolved.Success)
        {
            return new TriggerResult(false, null, job.Name, resolved.Error);
        }

        if (!string.IsNullOrEmpty(dedupKey))
        {
            var active = _buildRepo.FindActiveByDedupKey(job.Id, dedupKey);
            if (active is not null)
            {
                if (active.Status == BuildStatus.Running)
                {
                    // The exact same key is already in flight; queuing an identical duplicate to
                    // run immediately after would just redo the same work. Skip it.
                    return new TriggerResult(false, active, job.Name, "dedup");
                }
                if (job.QueuePolicy == "replace")
                {
                    // Queued/Waiting duplicate: discard it, the new request takes its place.
                    _buildRepo.UpdateStatus(active.Id, BuildStatus.Aborted, finishedAt: DateTimeOffset.Now.ToString("o"));
                }
                // queue_policy == "queue": leave the existing one and add the new one regardless.
            }
        }

        var build = _buildRepo.CreateQueued(job.Id, trigger, resolved.ParametersJson, dedupKey, commitSha, branch);
        _dispatcher.Signal();
        return new TriggerResult(true, build, job.Name, null);
    }

    /// <summary>
    /// Re-queues a past build's job with the same parameters and (for repo jobs) the same commit
    /// (spec §5 F5 "Rebuild ボタン", test spec PRM-003 "元ビルドと同一のパラメータで再実行"). Goes through
    /// the same Trigger() path as any other trigger source, so DedupKey/queue_policy still apply if a
    /// caller ever passes one - Rebuild itself never sets one, matching "キーなし投入は常に積まれる".
    /// </summary>
    public TriggerResult Rebuild(long buildId)
    {
        var original = _buildRepo.FindById(buildId);
        if (original is null)
        {
            return new TriggerResult(false, null, null, "build-not-found");
        }

        var job = _jobRepo.FindById(original.JobId);
        if (job is null)
        {
            return new TriggerResult(false, null, null, "job-not-found-or-disabled");
        }

        var parameters = ParseParametersJson(original.Parameters);
        return Trigger(job.Name, BuildTrigger.Rebuild, parameters, dedupKey: null, commitSha: original.CommitSha, branch: original.Branch);
    }

    private static Dictionary<string, string> ParseParametersJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}
