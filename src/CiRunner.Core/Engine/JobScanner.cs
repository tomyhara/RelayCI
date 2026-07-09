using System.Text.Json;
using CiRunner.Core.Data;
using CiRunner.Core.Models;
using CiRunner.Core.Paths;

namespace CiRunner.Core.Engine;

/// <summary>
/// Discovers server-mode jobs by scanning <c>jobs/&lt;name&gt;/pipeline.cipipe</c> and registers any
/// not yet present in the DB. If a sibling <c>jobs/&lt;name&gt;/job.json</c> exists, its fields
/// (parameters, cron/polling triggers, resources, queue policy, ...) are (re-)applied on every scan.
/// Stand-in for the F6 job-management admin UI, which is a later milestone.
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
            if (!File.Exists(Path.Combine(jobDir, "pipeline.cipipe")))
            {
                continue;
            }

            var configPath = Path.Combine(jobDir, "job.json");
            if (File.Exists(configPath))
            {
                var dto = ReadConfig(configPath);
                if (dto is not null)
                {
                    _jobRepo.UpsertConfiguredJob(ToInput(name, dto));
                    continue;
                }
            }

            _jobRepo.UpsertServerJob(name);
        }
    }

    private static JobConfigDto? ReadConfig(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<JobConfigDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException)
        {
            // Malformed job.json: fall back to bare auto-discovery rather than failing startup.
            return null;
        }
    }

    private static JobConfigInput ToInput(string name, JobConfigDto dto) => new(
        Name: name,
        RepoUrl: dto.RepoUrl,
        WorkspacePath: dto.WorkspacePath,
        PipelineSource: dto.PipelineSource ?? "server",
        PipelinePath: dto.PipelinePath ?? "pipeline.cipipe",
        // CI_-prefixed parameter names are reserved (spec §5 F1a) and rejected at definition time
        // rather than merely warned-about at trigger time.
        ParametersJson: JsonSerializer.Serialize((dto.Parameters ?? new List<JobParameterDef>())
            .Where(p => !ParameterResolver.IsReservedName(p.Name))
            .ToList()),
        CronSchedulesJson: JsonSerializer.Serialize(dto.CronSchedules ?? new List<string>()),
        PollingBranchesJson: dto.PollingBranches is null ? null : JsonSerializer.Serialize(dto.PollingBranches),
        ResourcesJson: JsonSerializer.Serialize(dto.Resources ?? new List<string>()),
        QueuePolicy: dto.QueuePolicy ?? "replace",
        TimeoutMinutes: dto.TimeoutMinutes,
        Retention: dto.Retention,
        ShellPath: dto.ShellPath,
        Enabled: dto.Enabled ?? true);

    private sealed class JobConfigDto
    {
        public string? RepoUrl { get; set; }
        public string? WorkspacePath { get; set; }
        public string? PipelineSource { get; set; }
        public string? PipelinePath { get; set; }
        public List<JobParameterDef>? Parameters { get; set; }
        public List<string>? CronSchedules { get; set; }
        public List<string>? PollingBranches { get; set; }
        public List<string>? Resources { get; set; }
        public string? QueuePolicy { get; set; }
        public int? TimeoutMinutes { get; set; }
        public int? Retention { get; set; }
        public string? ShellPath { get; set; }
        public bool? Enabled { get; set; }
    }
}
