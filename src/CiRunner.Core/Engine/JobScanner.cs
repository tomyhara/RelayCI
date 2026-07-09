using CiRunner.Core.Data;
using CiRunner.Core.Paths;

namespace CiRunner.Core.Engine;

/// <summary>
/// Discovers server-mode jobs by scanning <c>jobs/&lt;name&gt;/pipeline.cipipe</c> and registers any
/// not yet present in the DB. Stand-in for the F6 job-management admin UI, which is a later milestone.
/// </summary>
public sealed class JobScanner
{
    private readonly RunnerPaths _paths;
    private readonly JobRepository _jobRepo;

    public JobScanner(RunnerPaths paths, JobRepository jobRepo)
    {
        _paths = paths;
        _jobRepo = jobRepo;
    }

    public void ScanAndRegister()
    {
        if (!Directory.Exists(_paths.JobsDir))
        {
            return;
        }

        foreach (var jobDir in Directory.GetDirectories(_paths.JobsDir))
        {
            var name = Path.GetFileName(jobDir);
            if (File.Exists(Path.Combine(jobDir, "pipeline.cipipe")))
            {
                _jobRepo.UpsertServerJob(name);
            }
        }
    }
}
