namespace CiRunner.Core.Models;

/// <summary>Job parameter definition (spec §5 F1a): name, default, description, required.</summary>
public sealed class JobParameterDef
{
    public required string Name { get; set; }
    public string? Default { get; set; }
    public string? Description { get; set; }
    public bool Required { get; set; }
}

/// <summary>Input for JobRepository.UpsertConfiguredJob, parsed from jobs/&lt;name&gt;/job.json.</summary>
public sealed record JobConfigInput(
    string Name,
    string? RepoUrl,
    string? WorkspacePath,
    string PipelineSource,
    string PipelinePath,
    string ParametersJson,
    string CronSchedulesJson,
    string? PollingBranchesJson,
    string ResourcesJson,
    string QueuePolicy,
    int? TimeoutMinutes,
    int? Retention,
    string? ShellPath,
    bool Enabled);
