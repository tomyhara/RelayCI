namespace CiRunner.Core.Paths;

/// <summary>Directory layout per ci-runner-spec.md §4.</summary>
public sealed class RunnerPaths
{
    public string Root { get; }
    public string DataDir => Path.Combine(Root, "data");
    public string DbPath => Path.Combine(DataDir, "ci.db");
    public string JobsDir => Path.Combine(Root, "jobs");
    public string HooksDir => Path.Combine(Root, "hooks");
    public string WorkspacesDir => Path.Combine(Root, "workspaces");
    public string LogsDir => Path.Combine(Root, "logs");
    public string ControlFilesDir => Path.Combine(DataDir, "control");
    public string ResultsDir => Path.Combine(DataDir, "results");
    public string ArtifactsDir => Path.Combine(DataDir, "artifacts");

    public RunnerPaths(string root)
    {
        Root = root;
    }

    public string JobDir(string jobName) => Path.Combine(JobsDir, jobName);
    public string JobPipelinePath(string jobName) => Path.Combine(JobDir(jobName), "pipeline.cipipe");
    public string JobWorkspaceDir(string jobName) => Path.Combine(WorkspacesDir, jobName);
    public string JobLogsDir(string jobName) => Path.Combine(LogsDir, jobName);
    public string BuildLogPath(string jobName, int buildNumber) => Path.Combine(JobLogsDir(jobName), $"{buildNumber}.log");

    public void EnsureCreated()
    {
        foreach (var dir in new[] { DataDir, JobsDir, HooksDir, WorkspacesDir, LogsDir, ControlFilesDir, ResultsDir, ArtifactsDir })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
