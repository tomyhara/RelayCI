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
                if (!TryParse(expr, out var cron, out _))
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

    /// <summary>Parses a 5-field cron expression (spec §5 F1b "5 フィールド: 分 時 日 月 曜日"). Shared by
    /// the scheduler loop above and by the admin API (job create/update validation, TMR-005; and the
    /// next-run preview, E2E-019) so both agree on exactly which expressions are accepted.</summary>
    public static bool TryParse(string expr, out CronExpression cron, out string? error)
    {
        try
        {
            cron = CronExpression.Parse(expr);
            error = null;
            return true;
        }
        catch (CronFormatException ex)
        {
            cron = null!;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>The next <paramref name="count"/> occurrences of <paramref name="cron"/> strictly after
    /// <paramref name="from"/>, in local time. Used by the cron preview API (E2E-019 "次回発火時刻の
    /// プレビュー"); stops early if the expression has no further occurrences (Cronos returns null).</summary>
    public static List<DateTimeOffset> GetNextOccurrences(CronExpression cron, DateTimeOffset from, int count)
    {
        var results = new List<DateTimeOffset>(count);
        var cursor = from;
        for (var i = 0; i < count; i++)
        {
            var next = cron.GetNextOccurrence(cursor, TimeZoneInfo.Local);
            if (next is null)
            {
                break;
            }
            results.Add(next.Value);
            cursor = next.Value;
        }
        return results;
    }
}
