using CiRunner.Core.Data;
using CiRunner.Core.Engine;
using CiRunner.Core.Models;
using CiRunner.Core.Paths;

namespace CiRunner.Core.Tests.Support;

/// <summary>
/// Full engine wiring (paths, DB, hubs, runner) against a temp root, for L3-style integration tests
/// that launch the real powershell.exe + bootstrap.ps1 + CiRunner.psm1 (ci-runner-test-spec.md §3.1/§3.2).
/// </summary>
public sealed class EngineFixture : IDisposable
{
    public RunnerPaths Paths { get; }
    public CiDatabase Db { get; }
    public JobRepository Jobs { get; }
    public BuildRepository Builds { get; }
    public LiveLogHub LogHub { get; } = new();
    public GlobalEventHub EventHub { get; } = new();
    public BuildRunner Runner { get; }

    private readonly string _root;

    public EngineFixture()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ci-engine-{Guid.NewGuid()}");
        Paths = new RunnerPaths(_root);
        Paths.EnsureCreated();

        Db = new CiDatabase(Paths.DbPath);
        Db.Migrate();
        Jobs = new JobRepository(Db);
        Builds = new BuildRepository(Db);

        Runner = new BuildRunner(Paths, Builds, LogHub, EventHub, RepoPaths.BootstrapScript, "http://localhost:0");
    }

    public JobRecord CreateJob(string name, string pipelineContent)
    {
        Directory.CreateDirectory(Paths.JobDir(name));
        File.WriteAllText(Paths.JobPipelinePath(name), pipelineContent);
        return Jobs.UpsertServerJob(name);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort cleanup; temp dir GC is not test-critical
        }
    }
}
