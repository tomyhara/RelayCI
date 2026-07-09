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
    public HookRepository Hooks { get; }
    public HookRunRepository HookRuns { get; }
    public LiveLogHub LogHub { get; } = new();
    public GlobalEventHub EventHub { get; } = new();
    public BuildRunner Runner { get; }
    public BuildDispatcher Dispatcher { get; }
    public JobTriggerService TriggerService { get; }
    public HandlerRunner HandlerRunner { get; }
    public WebhookReceiver Webhook { get; }

    private readonly string _root;

    public EngineFixture(int executorLimit = 2, int handlerConcurrency = 4)
    {
        _root = Path.Combine(Path.GetTempPath(), $"ci-engine-{Guid.NewGuid()}");
        Paths = new RunnerPaths(_root);
        Paths.EnsureCreated();

        Db = new CiDatabase(Paths.DbPath);
        Db.Migrate();
        Jobs = new JobRepository(Db);
        Builds = new BuildRepository(Db);
        Hooks = new HookRepository(Db);
        HookRuns = new HookRunRepository(Db);

        Runner = new BuildRunner(Paths, Builds, LogHub, EventHub, RepoPaths.BootstrapScript, "http://localhost:0");
        Dispatcher = new BuildDispatcher(Builds, Jobs, Runner, EventHub, executorLimit);
        TriggerService = new JobTriggerService(Jobs, Builds, Dispatcher);
        HandlerRunner = new HandlerRunner(Paths, HookRuns, RepoPaths.BootstrapScript, "http://localhost:0");
        Webhook = new WebhookReceiver(Paths, Hooks, HookRuns, HandlerRunner, handlerConcurrency);
    }

    public JobRecord CreateJob(string name, string pipelineContent)
    {
        Directory.CreateDirectory(Paths.JobDir(name));
        File.WriteAllText(Paths.JobPipelinePath(name), pipelineContent);
        return Jobs.UpsertServerJob(name);
    }

    public HookRecord CreateHook(string name, string handlerContent, string? secret = null, int timeoutSec = 60)
    {
        Directory.CreateDirectory(Paths.HooksDir);
        File.WriteAllText(Paths.HookHandlerPath(name), handlerContent);
        return Hooks.UpsertDiscoveredHook(name, Paths.HookHandlerPath(name), secret, timeoutSec, enabled: true);
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
