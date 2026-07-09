using System.Text.Json;
using Cronos;
using CiRunner.Core.Data;
using CiRunner.Core.Models;
using Microsoft.Extensions.Hosting;

namespace CiRunner.Core.Engine;

/// <summary>
/// Cron trigger (spec §5 F1b): evaluates each job's cron_schedules on a short poll loop. Never
/// catches up on occurrences missed while the runner was stopped - the window only ever starts
/// at "now" as of this process's startup, so a long-downtime restart doesn't fire a stampede of
/// backlogged builds ("ミスファイアは追い掛けない").
/// </summary>
public sealed class CronScheduler : BackgroundService
{
    private readonly JobRepository _jobRepo;
    private readonly JobTriggerService _triggerService;
    private DateTimeOffset _windowStart = DateTimeOffset.Now;

    public CronScheduler(JobRepository jobRepo, JobTriggerService triggerService)
    {
        _jobRepo = jobRepo;
        _triggerService = triggerService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CheckOnce();
            }
            catch
            {
                // a malformed schedule on one job shouldn't stop the loop for everyone else.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void CheckOnce()
    {
        var now = DateTimeOffset.Now;
        var windowStart = _windowStart;
        _windowStart = now;

        foreach (var job in _jobRepo.ListEnabled())
        {
            List<string>? schedules;
            try
            {
                schedules = JsonSerializer.Deserialize<List<string>>(job.CronSchedules);
            }
            catch (JsonException)
            {
                continue;
            }
            if (schedules is null || schedules.Count == 0)
            {
                continue;
            }

            foreach (var expr in schedules)
            {
                if (!TryParse(expr, out var cron))
                {
                    continue;
                }

                var next = cron.GetNextOccurrence(windowStart, TimeZoneInfo.Local);
                if (next is { } occurrence && occurrence <= now)
                {
                    // Shared dedup key across a job's timer fires: if one is still Queued/Waiting
                    // when the schedule fires again, default queue_policy=replace swaps it out
                    // rather than piling up duplicates (spec §5 F1b).
                    _triggerService.Trigger(job.Name, BuildTrigger.Timer, requestedParameters: null, dedupKey: "timer");
                }
            }
        }
    }

    private static bool TryParse(string expr, out CronExpression cron)
    {
        try
        {
            cron = CronExpression.Parse(expr);
            return true;
        }
        catch (CronFormatException)
        {
            cron = null!;
            return false;
        }
    }
}
