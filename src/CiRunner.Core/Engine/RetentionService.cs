using CiRunner.Core.Data;
using CiRunner.Core.Models;
using CiRunner.Core.Paths;

namespace CiRunner.Core.Engine;

/// <summary>
/// Retention policy (spec §8): keeps only the most recent N builds per job (job.Retention, or the
/// system default), deleting older builds' log/result/artifact files and DB rows. Enforced after
/// each build completes rather than on a timer, since that's exactly when a job could newly exceed
/// its limit.
/// </summary>
public sealed class RetentionService
{
    private readonly RunnerPaths _paths;
    private readonly BuildRepository _buildRepo;
    private readonly SettingsRepository _settings;

    public RetentionService(RunnerPaths paths, BuildRepository buildRepo, SettingsRepository settings)
    {
        _paths = paths;
        _buildRepo = buildRepo;
        _settings = settings;
    }

    public void Enforce(JobRecord job)
    {
        var retention = job.Retention ?? _settings.GetInt("defaultRetention", 100);
        if (retention <= 0)
        {
            // 0/negative reads as "unset" here rather than "delete everything" - a real limit of
            // zero isn't a sensible retention policy and is far more likely a config mistake.
            return;
        }

        var builds = _buildRepo.ListByJob(job.Id, int.MaxValue); // already ordered newest-first
        var overLimit = builds.Skip(retention).Where(b => BuildStatus.IsTerminal(b.Status));

        foreach (var build in overLimit)
        {
            DeleteBuildFiles(job.Name, build);
            _buildRepo.DeleteBuild(build.Id);
        }
    }

    private void DeleteBuildFiles(string jobName, BuildRecord build)
    {
        TryDelete(() => File.Delete(_paths.BuildLogPath(jobName, build.Number)));
        TryDelete(() => File.Delete(Path.Combine(_paths.ControlFilesDir, $"{build.Id}.jsonl")));
        TryDelete(() => Directory.Delete(Path.Combine(_paths.ResultsDir, build.Id.ToString()), recursive: true));
        TryDelete(() => Directory.Delete(Path.Combine(_paths.ArtifactsDir, build.Id.ToString()), recursive: true));
    }

    private static void TryDelete(Action action)
    {
        try
        {
            action();
        }
        catch (IOException)
        {
            // best-effort cleanup; a file already gone or briefly locked isn't fatal.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
