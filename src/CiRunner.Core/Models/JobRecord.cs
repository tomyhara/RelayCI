namespace CiRunner.Core.Models;

public sealed class JobRecord
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public string? RepoUrl { get; set; }
    public string? WorkspacePath { get; set; }
    public string PipelineSource { get; set; } = "server";
    public string PipelinePath { get; set; } = ".ci/pipeline.cipipe";
    public string Parameters { get; set; } = "[]";
    public string CronSchedules { get; set; } = "[]";
    public string? PollingBranches { get; set; }
    public string Resources { get; set; } = "[]";
    public string QueuePolicy { get; set; } = "replace";
    public int? TimeoutMinutes { get; set; }
    public int? Retention { get; set; }
    public string? ShellPath { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Deleted { get; set; }
    public required string CreatedAt { get; set; }
}
