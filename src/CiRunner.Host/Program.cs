using System.Text.Json;
using CiRunner.Core.Config;
using CiRunner.Core.Data;
using CiRunner.Core.Engine;
using CiRunner.Core.Models;
using CiRunner.Core.Paths;

var config = ConfigLoader.Load(args);
var paths = new RunnerPaths(config.RootDir);
paths.EnsureCreated();

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://{config.Bind}:{config.Port}");

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(new CiDatabase(paths.DbPath));
builder.Services.AddSingleton<JobRepository>();
builder.Services.AddSingleton<BuildRepository>();
builder.Services.AddSingleton<SettingsRepository>();
builder.Services.AddSingleton<LiveLogHub>();
builder.Services.AddSingleton<GlobalEventHub>();
builder.Services.AddSingleton<JobScanner>();
builder.Services.AddSingleton(sp => new BuildRunner(
    paths,
    sp.GetRequiredService<BuildRepository>(),
    sp.GetRequiredService<LiveLogHub>(),
    sp.GetRequiredService<GlobalEventHub>(),
    Path.Combine(AppContext.BaseDirectory, "psmodule", "bootstrap.ps1"),
    $"http://localhost:{config.Port}"));
builder.Services.AddSingleton(sp =>
{
    var executorLimit = sp.GetRequiredService<SettingsRepository>().GetInt("executors", 2);
    return new BuildDispatcher(
        sp.GetRequiredService<BuildRepository>(),
        sp.GetRequiredService<JobRepository>(),
        sp.GetRequiredService<BuildRunner>(),
        sp.GetRequiredService<GlobalEventHub>(),
        executorLimit);
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<BuildDispatcher>());

var app = builder.Build();

app.Services.GetRequiredService<CiDatabase>().Migrate();
app.Services.GetRequiredService<JobScanner>().ScanAndRegister();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/jobs", (JobRepository jobRepo, BuildRepository buildRepo) =>
{
    var jobs = jobRepo.ListEnabled().Select(j => new
    {
        j.Name,
        j.Enabled,
        LatestBuild = MapBuildSummary(buildRepo.FindLatestByJob(j.Id)),
    });
    return Results.Ok(jobs);
});

app.MapGet("/api/jobs/{name}/builds", (string name, JobRepository jobRepo, BuildRepository buildRepo) =>
{
    var job = jobRepo.FindByName(name);
    if (job is null)
    {
        return Results.NotFound();
    }
    var builds = buildRepo.ListByJob(job.Id).Select(MapBuildSummary);
    return Results.Ok(builds);
});

app.MapPost("/api/jobs/{name}/trigger", (string name, JobRepository jobRepo, BuildRepository buildRepo, BuildDispatcher dispatcher) =>
{
    var job = jobRepo.FindByName(name);
    if (job is null || !job.Enabled)
    {
        return Results.NotFound();
    }
    var build = buildRepo.CreateQueued(job.Id, BuildTrigger.Manual, "{}", dedupKey: null);
    dispatcher.Signal();
    return Results.Ok(MapBuildSummary(build));
});

app.MapGet("/api/builds/{id:long}", (long id, BuildRepository buildRepo, JobRepository jobRepo) =>
{
    var build = buildRepo.FindById(id);
    if (build is null)
    {
        return Results.NotFound();
    }
    var job = jobRepo.FindById(build.JobId);
    var steps = buildRepo.ListSteps(id).Select(s => new
    {
        s.Seq,
        s.Name,
        s.Status,
        s.Post,
        s.Error,
        s.StartedAt,
        s.FinishedAt,
        s.LogOffsetStart,
        s.LogOffsetEnd,
    });
    return Results.Ok(new
    {
        build.Id,
        JobName = job?.Name,
        build.Number,
        build.Status,
        build.Trigger,
        build.Note,
        build.QueuedAt,
        build.StartedAt,
        build.FinishedAt,
        Steps = steps,
    });
});

app.MapGet("/api/builds/{id:long}/log/stream", async (long id, HttpContext ctx, BuildRepository buildRepo, JobRepository jobRepo, LiveLogHub logHub, RunnerPaths paths) =>
{
    var build = buildRepo.FindById(id);
    if (build is null)
    {
        return Results.NotFound();
    }
    var job = jobRepo.FindById(build.JobId);
    if (job is null)
    {
        return Results.NotFound();
    }

    var subscription = logHub.Subscribe(id);
    if (subscription is null)
    {
        // Not currently running: completed builds are served as a static file (spec §5 F5).
        var logPath = paths.BuildLogPath(job.Name, build.Number);
        return File.Exists(logPath) ? Results.File(logPath, "text/plain; charset=utf-8") : Results.NotFound();
    }

    var (backlog, reader) = subscription.Value;
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";

    await WriteSseAsync(ctx, backlog);
    try
    {
        await foreach (var line in reader.ReadAllAsync(ctx.RequestAborted))
        {
            await WriteSseAsync(ctx, line);
        }
    }
    catch (OperationCanceledException)
    {
        // client disconnected
    }

    return Results.Empty;
});

app.MapGet("/api/events", async (HttpContext ctx, GlobalEventHub eventHub) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    var reader = eventHub.Subscribe();
    try
    {
        await foreach (var payload in reader.ReadAllAsync(ctx.RequestAborted))
        {
            await ctx.Response.WriteAsync($"data: {payload}\n\n");
            await ctx.Response.Body.FlushAsync();
        }
    }
    catch (OperationCanceledException)
    {
        // client disconnected
    }
    finally
    {
        eventHub.Unsubscribe(reader);
    }
    return Results.Empty;
});

app.Run();

static async Task WriteSseAsync(HttpContext ctx, string text)
{
    await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(text)}\n\n");
    await ctx.Response.Body.FlushAsync();
}

static object? MapBuildSummary(BuildRecord? b) => b is null ? null : new
{
    b.Id,
    b.Number,
    b.Status,
    b.Trigger,
    b.Note,
    b.QueuedAt,
    b.StartedAt,
    b.FinishedAt,
};
