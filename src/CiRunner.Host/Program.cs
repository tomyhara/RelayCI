using System.Security.Claims;
using System.Text.Json;
using CiRunner.Core.Auth;
using CiRunner.Core.Config;
using CiRunner.Core.Data;
using CiRunner.Core.Engine;
using CiRunner.Core.Models;
using CiRunner.Core.Paths;
using CiRunner.Host.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

const string CookieScheme = "CiRunnerCookie";
const string ApiTokenScheme = "ApiToken";
const string SmartScheme = "Smart";

var config = ConfigLoader.Load(args);
var paths = new RunnerPaths(config.RootDir);
paths.EnsureCreated();

// auth.localUsers (spec §9 "テスト用認証") substitutes for a real LDAP server in automated tests.
// It must never be reachable in a Release build - refuse to start rather than silently ignore it.
#if !DEBUG
if (config.Auth.LocalUsers is { Count: > 0 })
{
    throw new InvalidOperationException("auth.localUsers is a Debug-build-only setting (spec §9); refusing to start a Release build with this key set.");
}
#endif

IAuthenticator authenticator =
#if DEBUG
    config.Auth.LocalUsers is { Count: > 0 }
        ? new LocalUsersAuthenticator(config.Auth.LocalUsers)
        : new LdapAuthenticator(config.Auth.Ldap);
#else
    new LdapAuthenticator(config.Auth.Ldap);
#endif

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://{config.Bind}:{config.Port}");

var serverUrl = $"http://localhost:{config.Port}";

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(new CiDatabase(paths.DbPath));
builder.Services.AddSingleton<JobRepository>();
builder.Services.AddSingleton<BuildRepository>();
builder.Services.AddSingleton<TestResultRepository>();
builder.Services.AddSingleton<ArtifactRepository>();
builder.Services.AddSingleton<SettingsRepository>();
builder.Services.AddSingleton<HookRepository>();
builder.Services.AddSingleton<HookRunRepository>();
builder.Services.AddSingleton<UserRoleRepository>();
builder.Services.AddSingleton<ApiTokenRepository>();
builder.Services.AddSingleton<AuditLogRepository>();
builder.Services.AddSingleton(authenticator);
builder.Services.AddSingleton<LiveLogHub>();
builder.Services.AddSingleton<GlobalEventHub>();
builder.Services.AddSingleton<JobScanner>();
builder.Services.AddSingleton<HookScanner>();

builder.Services.AddAuthentication(SmartScheme)
    .AddPolicyScheme(SmartScheme, "Cookie or Bearer", options =>
    {
        options.ForwardDefaultSelector = ctx =>
            ctx.Request.Headers.ContainsKey("Authorization") ? ApiTokenScheme : CookieScheme;
    })
    .AddCookie(CookieScheme, options =>
    {
        options.Cookie.Name = "ci_session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(config.Auth.SessionHours);
        options.SlidingExpiration = true;
        // This is an API, not a page app with server-side redirects: report 401/403 as status
        // codes instead of redirecting to a (nonexistent) /login page.
        options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenScheme, _ => { });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Viewer", p => p.RequireClaim("role", Role.Viewer, Role.Operator, Role.Admin))
    .AddPolicy("Operator", p => p.RequireClaim("role", Role.Operator, Role.Admin))
    .AddPolicy("Admin", p => p.RequireClaim("role", Role.Admin));

builder.Services.AddSingleton<IClaimsTransformation, RoleClaimsTransformation>();
builder.Services.AddSingleton(sp => new BuildRunner(
    paths,
    sp.GetRequiredService<BuildRepository>(),
    sp.GetRequiredService<TestResultRepository>(),
    sp.GetRequiredService<ArtifactRepository>(),
    sp.GetRequiredService<SettingsRepository>(),
    sp.GetRequiredService<LiveLogHub>(),
    sp.GetRequiredService<GlobalEventHub>(),
    Path.Combine(AppContext.BaseDirectory, "psmodule", "bootstrap.ps1"),
    serverUrl,
    config.Git.ExePath));
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddSingleton(sp =>
{
    var executorLimit = sp.GetRequiredService<SettingsRepository>().GetInt("executors", 2);
    return new BuildDispatcher(
        sp.GetRequiredService<BuildRepository>(),
        sp.GetRequiredService<JobRepository>(),
        sp.GetRequiredService<BuildRunner>(),
        sp.GetRequiredService<GlobalEventHub>(),
        executorLimit,
        sp.GetRequiredService<RetentionService>());
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<BuildDispatcher>());
builder.Services.AddSingleton<JobTriggerService>();
builder.Services.AddSingleton(sp => new HandlerRunner(
    paths,
    sp.GetRequiredService<HookRunRepository>(),
    Path.Combine(AppContext.BaseDirectory, "psmodule", "bootstrap.ps1"),
    serverUrl));
builder.Services.AddSingleton(sp =>
{
    var concurrency = sp.GetRequiredService<SettingsRepository>().GetInt("handlerConcurrency", 4);
    return new WebhookReceiver(
        paths,
        sp.GetRequiredService<HookRepository>(),
        sp.GetRequiredService<HookRunRepository>(),
        sp.GetRequiredService<HandlerRunner>(),
        concurrency);
});
builder.Services.AddSingleton(sp =>
{
    var intervalSec = sp.GetRequiredService<SettingsRepository>().GetInt("pollingIntervalSec", 60);
    return new PollingService(
        sp.GetRequiredService<JobRepository>(),
        sp.GetRequiredService<JobTriggerService>(),
        config.Git.ExePath,
        intervalSec);
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<PollingService>());
builder.Services.AddSingleton<CronScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CronScheduler>());

var app = builder.Build();

app.Services.GetRequiredService<CiDatabase>().Migrate();
app.Services.GetRequiredService<JobScanner>().ScanAndRegister();
app.Services.GetRequiredService<HookScanner>().ScanAndRegister();

// Initial admin bootstrap (spec §9): only ever applied while user_roles is empty, so this is a
// one-time first-run action, not something that re-grants admin on every restart.
{
    var userRoles = app.Services.GetRequiredService<UserRoleRepository>();
    if (userRoles.IsEmpty())
    {
        foreach (var username in config.Auth.InitialAdmins)
        {
            userRoles.Upsert(username, Role.Admin, "system:initialAdmins");
        }
    }
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/api/login", async (LoginRequest? body, IAuthenticator auth, UserRoleRepository roles, RunnerConfig cfg, HttpContext ctx) =>
{
    if (string.IsNullOrEmpty(body?.Username) || string.IsNullOrEmpty(body.Password))
    {
        return Results.BadRequest(new { error = "username and password are required" });
    }

    AuthResult? result;
    try
    {
        result = await auth.AuthenticateAsync(body.Username, body.Password, ctx.RequestAborted);
    }
    catch (Exception)
    {
        // LDAP server unreachable etc: fail closed, never fail open (spec §9 "既存セッション
        // Cookie は有効期限まで有効" implies new logins are simply refused, not silently allowed).
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    if (result is null)
    {
        return Results.Unauthorized();
    }

    var roleRecord = roles.Find(result.Username);
    var role = roleRecord?.Role ?? (cfg.Auth.DefaultRole == "deny" ? null : cfg.Auth.DefaultRole);
    if (role is null)
    {
        return Results.Forbid();
    }

    var claims = new List<Claim> { new(ClaimTypes.Name, result.Username), new("role", role) };
    if (result.DisplayName is not null) claims.Add(new Claim("displayName", result.DisplayName));
    if (result.Mail is not null) claims.Add(new Claim("mail", result.Mail));

    await ctx.SignInAsync(CookieScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieScheme)), new AuthenticationProperties
    {
        IsPersistent = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddHours(cfg.Auth.SessionHours),
    });

    return Results.Ok(new { username = result.Username, displayName = result.DisplayName, mail = result.Mail, role });
}).AllowAnonymous();

app.MapPost("/api/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieScheme);
    return Results.Ok();
}).AllowAnonymous();

app.MapGet("/api/me", (ClaimsPrincipal user) =>
{
    if (user.Identity is not { IsAuthenticated: true })
    {
        return Results.Ok(new { authenticated = false });
    }
    return Results.Ok(new
    {
        authenticated = true,
        username = user.Identity.Name,
        displayName = user.FindFirst("displayName")?.Value,
        mail = user.FindFirst("mail")?.Value,
        role = user.FindFirst("role")?.Value,
        authMethod = user.FindFirst("authMethod")?.Value ?? "session",
    });
}).AllowAnonymous();

app.MapGet("/api/users", (UserRoleRepository roles) => Results.Ok(roles.ListAll()))
    .RequireAuthorization("Admin");

app.MapPost("/api/users/{username}/role", (string username, RoleAssignRequest? body, UserRoleRepository roles, AuditLogRepository audit, ClaimsPrincipal actor) =>
{
    if (body?.Role is not (Role.Admin or Role.Operator or Role.Viewer))
    {
        return Results.BadRequest(new { error = "role must be admin, operator, or viewer" });
    }
    var before = roles.Find(username);
    if (before?.Role == Role.Admin && body.Role != Role.Admin && roles.CountAdmins() <= 1)
    {
        return Results.BadRequest(new { error = "cannot demote the last admin" });
    }
    var actorName = actor.Identity!.Name!;
    roles.Upsert(username, body.Role, actorName);
    audit.Record(actorName, "role.assign", username,
        before is null ? null : JsonSerializer.Serialize(new { role = before.Role }),
        JsonSerializer.Serialize(new { role = body.Role }));
    return Results.Ok();
}).RequireAuthorization("Admin");

app.MapDelete("/api/users/{username}/role", (string username, UserRoleRepository roles, AuditLogRepository audit, ClaimsPrincipal actor) =>
{
    var before = roles.Find(username);
    if (before is null)
    {
        return Results.NotFound();
    }
    if (before.Role == Role.Admin && roles.CountAdmins() <= 1)
    {
        return Results.BadRequest(new { error = "cannot remove the last admin" });
    }
    var actorName = actor.Identity!.Name!;
    roles.Delete(username);
    audit.Record(actorName, "role.remove", username, JsonSerializer.Serialize(new { role = before.Role }), null);
    return Results.Ok();
}).RequireAuthorization("Admin");

app.MapGet("/api/tokens", (ApiTokenRepository tokens) => Results.Ok(tokens.ListAll().Select(t => new
{
    t.Id,
    t.Name,
    t.Role,
    t.CreatedAt,
    t.CreatedBy,
    t.LastUsedAt,
    t.RevokedAt,
}))).RequireAuthorization("Admin");

app.MapPost("/api/tokens", (IssueTokenRequest? body, ApiTokenRepository tokens, AuditLogRepository audit, ClaimsPrincipal actor) =>
{
    if (string.IsNullOrWhiteSpace(body?.Name) || body.Role is not (Role.Admin or Role.Operator or Role.Viewer))
    {
        return Results.BadRequest(new { error = "name and a valid role are required" });
    }
    var actorName = actor.Identity!.Name!;
    var rawToken = ApiTokenHasher.GenerateToken();
    var id = tokens.Insert(body.Name, ApiTokenHasher.Hash(rawToken), body.Role, actorName);
    audit.Record(actorName, "token.issue", body.Name, null, JsonSerializer.Serialize(new { role = body.Role }));
    return Results.Ok(new { id, token = rawToken }); // shown once; only the hash is ever stored
}).RequireAuthorization("Admin");

app.MapDelete("/api/tokens/{id:long}", (long id, ApiTokenRepository tokens, AuditLogRepository audit, ClaimsPrincipal actor) =>
{
    if (!tokens.Revoke(id))
    {
        return Results.NotFound();
    }
    audit.Record(actor.Identity!.Name!, "token.revoke", id.ToString(), null, null);
    return Results.Ok();
}).RequireAuthorization("Admin");

app.MapGet("/api/audit", (AuditLogRepository audit) => Results.Ok(audit.ListRecent(200)))
    .RequireAuthorization("Admin");

app.MapGet("/api/jobs", (JobRepository jobRepo, BuildRepository buildRepo) =>
{
    var jobs = jobRepo.ListEnabled().Select(j => new
    {
        j.Name,
        j.Enabled,
        j.RepoUrl,
        j.PipelineSource,
        LatestBuild = MapBuildSummary(buildRepo.FindLatestByJob(j.Id)),
        RecentBuilds = buildRepo.ListByJob(j.Id, 10).Select(b => new { b.Number, b.Status }),
    });
    return Results.Ok(jobs);
}).RequireAuthorization("Viewer");

app.MapGet("/api/queue", (BuildRepository buildRepo, JobRepository jobRepo) =>
{
    var queued = buildRepo.ListQueued();
    var result = queued.Select((b, i) =>
    {
        var job = jobRepo.FindById(b.JobId);
        return new
        {
            Position = i + 1,
            b.Id,
            JobName = job?.Name,
            b.Number,
            b.Status,
            b.Trigger,
            b.QueuedAt,
        };
    });
    return Results.Ok(result);
}).RequireAuthorization("Viewer");

app.MapGet("/api/status", (SettingsRepository settings, BuildRepository buildRepo, RunnerConfig cfg) =>
{
    return Results.Ok(new
    {
        Executors = settings.GetInt("executors", 2),
        ActiveExecutors = buildRepo.CountByStatus(BuildStatus.Running),
        QueueLength = buildRepo.CountByStatus(BuildStatus.Queued) + buildRepo.CountByStatus(BuildStatus.Waiting),
        Port = cfg.Port,
    });
}).RequireAuthorization("Viewer");

app.MapGet("/api/jobs/{name}/builds", (string name, JobRepository jobRepo, BuildRepository buildRepo) =>
{
    var job = jobRepo.FindByName(name);
    if (job is null)
    {
        return Results.NotFound();
    }
    var builds = buildRepo.ListByJob(job.Id).Select(MapBuildSummary);
    return Results.Ok(builds);
}).RequireAuthorization("Viewer");

app.MapPost("/api/jobs/{name}/trigger", (string name, TriggerRequest? body, JobTriggerService triggerService) =>
{
    var result = triggerService.Trigger(name, BuildTrigger.Manual, body?.Parameters, dedupKey: null);
    if (!result.Queued && result.Reason == "job-not-found-or-disabled")
    {
        return Results.NotFound();
    }
    if (!result.Queued)
    {
        return Results.BadRequest(new { error = result.Reason });
    }
    return Results.Ok(MapBuildSummary(result.Build));
}).RequireAuthorization("Operator");

app.MapPost("/api/internal/start-job/{name}", (string name, StartJobRequest? body, JobTriggerService triggerService, HookRunRepository hookRunRepo) =>
{
    var result = triggerService.Trigger(name, BuildTrigger.Hook, body?.Parameters, body?.DedupKey);
    if (!result.Queued && result.Reason == "job-not-found-or-disabled")
    {
        return Results.NotFound();
    }

    if (result.Queued && body?.HookRunId is { } hookRunId && result.Build is not null)
    {
        hookRunRepo.AppendTriggeredBuild(hookRunId, result.Build.Id);
    }

    return Results.Ok(new
    {
        Queued = result.Queued,
        BuildNumber = result.Build?.Number,
        Url = result.Build is null ? null : $"{serverUrl}/#/builds/{result.Build.Id}",
        Reason = result.Reason,
    });
}).AllowAnonymous();

app.MapPost("/api/webhook/{name}", async (string name, HttpRequest request, WebhookReceiver receiver) =>
{
    using var ms = new MemoryStream();
    await request.Body.CopyToAsync(ms);
    var body = ms.ToArray();
    var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

    var result = await receiver.ReceiveAsync(name, body, headers, request.HttpContext.RequestAborted);
    return result switch
    {
        WebhookReceiveResult.HookNotFound => Results.NotFound(),
        WebhookReceiveResult.Unauthorized => Results.Unauthorized(),
        _ => Results.Ok(),
    };
}).AllowAnonymous();

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
}).RequireAuthorization("Viewer");

app.MapGet("/api/builds/{id:long}/tests", (long id, TestResultRepository testRepo) =>
{
    var tests = testRepo.ListByBuild(id).Select(t => new { t.Suite, t.Name, t.Status, t.DurationMs, t.Message });
    return Results.Ok(tests);
}).RequireAuthorization("Viewer");

app.MapGet("/api/builds/{id:long}/artifacts", (long id, ArtifactRepository artifactRepo) =>
{
    var artifacts = artifactRepo.ListByBuild(id).Select(a => new { a.Id, a.Path, a.Size });
    return Results.Ok(artifacts);
}).RequireAuthorization("Viewer");

app.MapGet("/api/builds/{id:long}/artifacts/{artifactId:long}/download", (long id, long artifactId, ArtifactRepository artifactRepo, RunnerPaths paths) =>
{
    // artifactId is a DB-assigned integer, not a client-supplied path, so there is nothing to
    // sanitize: the on-disk path always comes from our own artifact record, never from the request.
    var artifact = artifactRepo.ListByBuild(id).FirstOrDefault(a => a.Id == artifactId);
    if (artifact is null)
    {
        return Results.NotFound();
    }
    var fullPath = Path.Combine(paths.ArtifactsDir, id.ToString(), artifact.Path);
    if (!File.Exists(fullPath))
    {
        return Results.NotFound();
    }
    return Results.File(fullPath, "application/octet-stream", Path.GetFileName(artifact.Path));
}).RequireAuthorization("Viewer");

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
}).RequireAuthorization("Viewer");

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
}).RequireAuthorization("Viewer");

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

sealed class TriggerRequest
{
    public Dictionary<string, string>? Parameters { get; set; }
}

sealed class StartJobRequest
{
    public Dictionary<string, string>? Parameters { get; set; }
    public string? DedupKey { get; set; }
    public long? HookRunId { get; set; }
}

sealed class LoginRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
}

sealed class RoleAssignRequest
{
    public string? Role { get; set; }
}

sealed class IssueTokenRequest
{
    public string? Name { get; set; }
    public string? Role { get; set; }
}
