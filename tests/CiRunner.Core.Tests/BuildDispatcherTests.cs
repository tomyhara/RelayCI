using CiRunner.Core.Engine;
using CiRunner.Core.Models;
using CiRunner.Core.Tests.Support;
using Xunit;

namespace CiRunner.Core.Tests;

/// <summary>
/// L3 dispatcher tests (ci-runner-test-spec.md §3.1 ENG-002/003/005): Executor-count gating and
/// same-job serialization, driven through the real BuildDispatcher + BuildRunner + powershell.exe.
/// </summary>
public class BuildDispatcherTests
{
    private static async Task WaitUntilTerminalAsync(EngineFixture fx, long buildId, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            var build = fx.Builds.FindById(buildId)!;
            if (BuildStatus.IsTerminal(build.Status))
            {
                return;
            }
            await Task.Delay(100, CancellationToken.None);
        }
        throw new TimeoutException($"Build {buildId} did not reach a terminal state in time.");
    }

    [Fact]
    public async Task Dispatcher_ExecutorOne_SameJobBuildsRunSerially_ENG002()
    {
        using var fx = new EngineFixture();
        var job = fx.CreateJob("serial", """
            Stage "Work" { Start-Sleep -Milliseconds 800 }
            """);
        var dispatcher = new BuildDispatcher(fx.Builds, fx.Jobs, fx.Runner, fx.EventHub, executorLimit: 1);
        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            var b1 = fx.Builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);
            var b2 = fx.Builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);
            dispatcher.Signal();

            await WaitUntilTerminalAsync(fx, b1.Id, TimeSpan.FromSeconds(15));
            await WaitUntilTerminalAsync(fx, b2.Id, TimeSpan.FromSeconds(15));

            var r1 = fx.Builds.FindById(b1.Id)!;
            var r2 = fx.Builds.FindById(b2.Id)!;
            Assert.Equal(BuildStatus.Success, r1.Status);
            Assert.Equal(BuildStatus.Success, r2.Status);

            var b1Finished = DateTimeOffset.Parse(r1.FinishedAt!);
            var b2Started = DateTimeOffset.Parse(r2.StartedAt!);
            Assert.True(b2Started >= b1Finished, $"Expected build2 to start ({b2Started:o}) after build1 finished ({b1Finished:o}) - same job must be serial.");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilRunningAsync(EngineFixture fx, long buildId, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (fx.Builds.FindById(buildId)!.Status == BuildStatus.Running)
            {
                return;
            }
            await Task.Delay(50, CancellationToken.None);
        }
        throw new TimeoutException($"Build {buildId} did not start running in time.");
    }

    // F6: "executors" is read live from SettingsRepository on every dispatch tick, so a settings-screen
    // change applies without a runner restart (spec §5 F6 "再起動不要で即時反映").
    [Fact]
    public async Task Dispatcher_ExecutorLimitChangedViaSettings_TakesEffectWithoutRestart()
    {
        using var fx = new EngineFixture();
        var jobA = fx.CreateJob("live-a", """Stage "Work" { Start-Sleep -Milliseconds 2500 }""");
        var jobB = fx.CreateJob("live-b", """Stage "Work" { Start-Sleep -Milliseconds 200 }""");
        fx.Settings.Set("executors", "1");
        var dispatcher = new BuildDispatcher(fx.Builds, fx.Jobs, fx.Runner, fx.EventHub, executorLimit: 1, retentionService: null, settings: fx.Settings);
        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            var bA = fx.Builds.CreateQueued(jobA.Id, BuildTrigger.Manual, "{}", null);
            dispatcher.Signal();
            await WaitUntilRunningAsync(fx, bA.Id, TimeSpan.FromSeconds(10));

            var bB = fx.Builds.CreateQueued(jobB.Id, BuildTrigger.Manual, "{}", null);
            dispatcher.Signal();
            await Task.Delay(400);
            Assert.Equal(BuildStatus.Queued, fx.Builds.FindById(bB.Id)!.Status); // still capped at 1 while A runs

            fx.Settings.Set("executors", "2");
            dispatcher.Signal();
            await WaitUntilTerminalAsync(fx, bB.Id, TimeSpan.FromSeconds(10));

            var rA = fx.Builds.FindById(bA.Id)!;
            var rB = fx.Builds.FindById(bB.Id)!;
            Assert.Equal(BuildStatus.Success, rB.Status);
            // A (2.5s sleep) is still running when B (0.2s sleep) finishes - only possible if B started
            // concurrently with A, which the old fixed-at-construction executorLimit=1 would never allow.
            Assert.Equal(BuildStatus.Running, rA.Status);
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Dispatcher_ExecutorTwo_DifferentJobsRunConcurrently_ENG003()
    {
        using var fx = new EngineFixture();
        var jobA = fx.CreateJob("parallel-a", """Stage "Work" { Start-Sleep -Milliseconds 1000 }""");
        var jobB = fx.CreateJob("parallel-b", """Stage "Work" { Start-Sleep -Milliseconds 1000 }""");
        var dispatcher = new BuildDispatcher(fx.Builds, fx.Jobs, fx.Runner, fx.EventHub, executorLimit: 2);
        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            var bA = fx.Builds.CreateQueued(jobA.Id, BuildTrigger.Manual, "{}", null);
            var bB = fx.Builds.CreateQueued(jobB.Id, BuildTrigger.Manual, "{}", null);
            dispatcher.Signal();

            await WaitUntilTerminalAsync(fx, bA.Id, TimeSpan.FromSeconds(15));
            await WaitUntilTerminalAsync(fx, bB.Id, TimeSpan.FromSeconds(15));

            var rA = fx.Builds.FindById(bA.Id)!;
            var rB = fx.Builds.FindById(bB.Id)!;

            var aStarted = DateTimeOffset.Parse(rA.StartedAt!);
            var aFinished = DateTimeOffset.Parse(rA.FinishedAt!);
            var bStarted = DateTimeOffset.Parse(rB.StartedAt!);
            var bFinished = DateTimeOffset.Parse(rB.FinishedAt!);

            // Overlap check: they ran concurrently if each started before the other finished.
            Assert.True(aStarted < bFinished && bStarted < aFinished,
                $"Expected concurrent execution. A=[{aStarted:o},{aFinished:o}] B=[{bStarted:o},{bFinished:o}]");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Dispatcher_ExecutorOne_TwoDifferentJobs_StillLimitedToOneAtATime()
    {
        using var fx = new EngineFixture();
        var jobA = fx.CreateJob("limit-a", """Stage "Work" { Start-Sleep -Milliseconds 800 }""");
        var jobB = fx.CreateJob("limit-b", """Stage "Work" { Start-Sleep -Milliseconds 800 }""");
        var dispatcher = new BuildDispatcher(fx.Builds, fx.Jobs, fx.Runner, fx.EventHub, executorLimit: 1);
        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            var bA = fx.Builds.CreateQueued(jobA.Id, BuildTrigger.Manual, "{}", null);
            var bB = fx.Builds.CreateQueued(jobB.Id, BuildTrigger.Manual, "{}", null);
            dispatcher.Signal();

            await WaitUntilTerminalAsync(fx, bA.Id, TimeSpan.FromSeconds(15));
            await WaitUntilTerminalAsync(fx, bB.Id, TimeSpan.FromSeconds(15));

            var rA = fx.Builds.FindById(bA.Id)!;
            var rB = fx.Builds.FindById(bB.Id)!;
            var aFinished = DateTimeOffset.Parse(rA.FinishedAt!);
            var bFinished = DateTimeOffset.Parse(rB.FinishedAt!);
            var aStarted = DateTimeOffset.Parse(rA.StartedAt!);
            var bStarted = DateTimeOffset.Parse(rB.StartedAt!);

            // With a single executor, one build's window must not overlap the other's.
            var noOverlap = bStarted >= aFinished || aStarted >= bFinished;
            Assert.True(noOverlap, $"Expected non-overlapping execution with executorLimit=1. A=[{aStarted:o},{aFinished:o}] B=[{bStarted:o},{bFinished:o}]");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }
}
