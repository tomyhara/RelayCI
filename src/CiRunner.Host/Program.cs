using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
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

// Job/hook names flow directly into on-disk paths (jobs/<name>/, hooks/<name>.cipipe) - restrict to
// a safe charset so an admin-supplied name can never escape the jobs/hooks directory.
var JobNamePattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]*$");

const string HookHandlerTemplate = """
    # Webhook handler (spec §5 F1/F6). Restricted DSL surface: Get-HookPayload, Get-HookHeader,
    # Start-CiJob, Exec only.
    $payload = Get-HookPayload | ConvertFrom-Json
    # Start-CiJob -Name "some-job" -Parameters @{ ref = $payload.ref }
    """;

const string PipelineTemplate = """
    Stage "Build" {
        Exec { Write-Host "TODO: replace with real build steps" }
    }
    """;

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
builder.Services.AddSingleton<ResourceDefRepository>();
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
builder.Services.AddSingleton<ResourceLockManager>();
builder.Services.AddSingleton(sp =>
{
    var executorLimit = sp.GetRequiredService<SettingsRepository>().GetInt("executors", 2);
    return new BuildDispatcher(
        sp.GetRequiredService<BuildRepository>(),
        sp.GetRequiredService<JobRepository>(),
        sp.GetRequiredService<BuildRunner>(),
        sp.GetRequiredService<GlobalEventHub>(),
        executorLimit,
        sp.GetRequiredService<RetentionService>(),
        sp.GetRequiredService<SettingsRepository>(),
        sp.GetRequiredService<ResourceLockManager>());
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
    var settings = sp.GetRequiredService<SettingsRepository>();
    var intervalSec = settings.GetInt("pollingIntervalSec", 60);
    return new PollingService(
        sp.GetRequiredService<JobRepository>(),
        sp.GetRequiredService<JobTriggerService>(),
        config.Git.ExePath,
        intervalSec,
        settings);
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

// ---- F6: system settings (spec §5 F6 "システム設定") ----

app.MapGet("/api/settings", (SettingsRepository settings) => Results.Ok(new
{
    executors = settings.GetInt("executors", 2),
    defaultTimeoutMinutes = settings.GetInt("defaultTimeoutMinutes", 60),
    defaultRetention = settings.GetInt("defaultRetention", 100),
    pollingIntervalSec = settings.GetInt("pollingIntervalSec", 60),
    testResultMode = settings.GetString("testResultMode", "strict"),
    handlerConcurrency = settings.GetInt("handlerConcurrency", 4),
})).RequireAuthorization("Admin");

app.MapPost("/api/settings", (SettingsUpdateRequest? body, SettingsRepository settings, AuditLogRepository audit, ClaimsPrincipal actor) =>
{
    if (body is null) return Results.BadRequest(new { error = "request body required" });
    if (body.Executors is { } ex && (ex < 1 || ex > 16)) return Results.BadRequest(new { error = "executors must be between 1 and 16" });
    if (body.TestResultMode is { } trm && trm is not ("strict" or "exit-code-only")) return Results.BadRequest(new { error = "testResultMode must be strict or exit-code-only" });

    var actorName = actor.Identity!.Name!;
    var before = new Dictionary<string, string>();
    var after = new Dictionary<string, string>();
    void Apply(string key, string? value)
    {
        if (value is null) return;
        before[key] = settings.GetString(key, "");
        settings.Set(key, value, actorName);
        after[key] = value;
    }
    Apply("executors", body.Executors?.ToString());
    Apply("defaultTimeoutMinutes", body.DefaultTimeoutMinutes?.ToString());
    Apply("defaultRetention", body.DefaultRetention?.ToString());
    Apply("pollingIntervalSec", body.PollingIntervalSec?.ToString());
    Apply("testResultMode", body.TestResultMode);
    Apply("handlerConcurrency", body.HandlerConcurrency?.ToString());

    if (after.Count > 0)
    {
        audit.Record(actorName, "settings.update", null, JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
    }
    return Results.Ok();
}).RequireAuthorization("Admin");

// ---- F6: job management (spec §5 F6 "ジョブ設定") ----
// Jobs remain file-backed (jobs/<name>/job.json + pipeline.cipipe): every write here also mirrors
// to disk so a later restart's JobScanner rescan reproduces the same state instead of clobbering it.

app.MapGet("/api/admin/jobs", (JobRepository jobRepo) => Results.Ok(jobRepo.ListEnabled()))
    .RequireAuthorization("Admin");

app.MapGet("/api/admin/jobs/{name}", (string name, JobRepository jobRepo) =>
{
    var job = jobRepo.FindByName(name);
    return job is null ? Results.NotFound() : Results.Ok(job);
}).RequireAuthorization("Admin");

app.MapPost("/api/admin/jobs", (JobAdminRequest? body, JobRepository jobRepo, RunnerPaths pathsSvc, AuditLogRepository audit, ClaimsPrincipal actor) =>
{
    if (string.IsNullOrWhiteSpace(body?.Name) || !JobNamePattern.IsMatch(body.Name))
    {
        return Results.BadRequest(new { error = "name is required and may only contain letters, digits, '.', '_', '-'" });
    }
    if (jobRepo.FindByName(body.Name) is not null)
    {
        return Results.Conflict(new { error = $"job '{body.Name}' already exists" });
    }
    var validationError = ValidateJobRequest(body);
    if (validationError is not null) return Results.BadRequest(new { error = validationError });

    jobRepo.Undelete(body.Name); // reactivate if this name belonged to a previously-deleted job
    var job = ApplyJobConfig(pathsSvc, jobRepo, body.Name, body);
    audit.Record(actor.Identity!.Name!, "job.create", body.Name, null, JsonSerializer.Serialize(job));
    return Results.Ok(job);
}).RequireAuthorization("Admin");

app.MapPut("/api/admin/jobs/{name}", (string name, JobAdminRequest? body, JobRepository jobRepo, RunnerPaths pathsSvc, AuditLogRepository audit, ClaimsPrincipal actor) =>
{
    if (body is null) return Results.BadRequest(new { error = "request body required" });
    var existing = jobRepo.FindByName(name);
    if (existing is null) return Results.NotFound();
    var validationError = ValidateJobRequest(body);
    if (validationError is not null) return Results.BadRequest(new { error = validationError });

    var beforeJson = JsonSerializer.Serialize(existing);
    var job = ApplyJobConfig(pathsSvc, jobRepo, name, body);
    audit.Record(actor.Identity!.Name!, "job.update", name, beforeJson, JsonSerializer.Serialize(job));
    return Results.Ok(job);
}).RequireAuthorization("Admin");

app.MapDelete("/api/admin/jobs/{name}", (string name, JobRepository jobRepo, AuditLogRepository audit, ClaimsPrincipal actor) =>
{
    var existing = jobRepo.FindByName(name);
    if (existing is null) return Results.NotFound();
    jobRepo.SoftDelete(name);
    audit.Record(actor.Identity!.Name!, "job.delete", name, JsonSerializer.Serialize(existing), null);
    return Results.Ok();
}).RequireAuthorization("Admin");

app.MapGet("/api/admin/jobs/{name}/export", (string name, JobRepository jobRepo) =>
{
    var job = jobRepo.FindByName(name);
    return job is null ? Results.NotFound() : Results.Ok(ToExportDto(job));
}).RequireAuthorization("Admin");

app.MapPost("/api/admin/jobs/import", (JobAdminRequest? body, JobRepository jobRepo, RunnerPaths pathsSvc, AuditLogRepository audit, ClaimsPrincipal actor) =>
{
    if (string.IsNullOrWhiteSpace(body?.Name) || !JobNamePattern.IsMatch(body.Name))
    {
        return Results.BadRequest(new { error = "name is required and may only contain letters, digits, '.', '_', '-'" });
    }
    var validationError = ValidateJobRequest(body);
    if (validationError is not null) return Results.BadRequest(new { error = validationError });

    var existing = jobRepo.FindByName(body.Name);
    jobRepo.Undelete(body.Name); // reactivate if this name belonged to a previously-deleted job
    var job = ApplyJobConfig(pathsSvc, jobRepo, body.Name, body);
    audit.Record(actor.Identity!.Name!, existing is null ? "job.create" : "job.update", body.Name,
        existing is null ? null : JsonSerializer.Serialize(existing), JsonSerializer.Serialize(job));
    return Results.Ok(job);
}).RequireAuthorization("Admin");

// ---- F1b: cron next-run preview (spec §5 F1b "次回発火時刻のプレビュー", test-spec E2E-019) ----
// A standalone endpoint (rather than tied to a job name) so the job form can preview a schedule the
// user just typed, before it's ever saved.
app.MapGet("/api/admin/cron/preview", (string? expr) =>
{
    if (string.IsNullOrWhiteSpace(expr))
    {
        return Results.BadRequest(new { error = "expr is required" });
    }
    if (!CronScheduler.TryParse(expr, out var cron, out var error))
    {
        return Results.BadRequest(new { error });
    }
    var occurrences = CronScheduler.GetNextOccurrences(cron, DateTimeOffset.Now, 3);
    return Results.Ok(new { occurrences });
}).RequireAuthorization("Admin");

// ---- F6: hook management (spec §5 F6 "フック管理") ----

app.MapGet("/api/admin/hooks", (HookRepository hookRepo) => Results.Ok(hookRepo.ListEnabled().Select(MapHookForAdmin)))
    .RequireAuthorization("Admin");

app.MapGet("/api/admin/hooks/{name}", (string name, HookRepository hookRepo) =>
{
    var hook = hookRepo.FindByName(name);
    return hook is null ? Results.NotFound() : Results.Ok(MapHookForAdmin(hook));
}).RequireAuthorization("Admin");

app.MapPost("/api/admin/hooks", (HookAdminCreateRequest? body, HookRepository hookRepo, RunnerPaths pathsSvc, AuditLogRepository audit, ClaimsPrincipal actor) =>
{
    if (string.IsNullOrWhiteSpace(body?.Name) || !JobNamePattern.IsMatch(body.Name))
    {
        return Results.BadRequest(new { error = "name is required and may only contain letters, digits, '.', '_', '-'" });
    }
    if (hookRepo.FindByName(body.Name) is not null)
    {
        return Results.Conflict(new { error = $"hook '{body.Name}' already exists" });
    }

    var handlerPath = pathsSvc.HookHandlerPath(body.Name);
    Directory.CreateDirectory(pathsSvc.HooksDir);
    if (!File.Exists(handlerPath))
    {
        File.WriteAllText(handlerPath, HookHandlerTemplate);
    }
    WriteHookConfig(pathsSvc, body.Name, body.Secret, body.TimeoutSec ?? 60, body.Enabled ?? true);
    hookRepo.Undelete(body.Name); // reactivate if this name belonged to a previously-deleted hook
    var hook = hookRepo.UpsertDiscoveredHook(body.Name, handlerPath, body.Secret, body.TimeoutSec ?? 60, body.Enabled ?? true);
    audit.Record(actor.Identity!.Name!, "hook.create", body.Name, null, JsonSerializer.Serialize(MapHookForAdmin(hook)));
    return Results.Ok(MapHookForAdmin(hook));
}).RequireAuthorization("Admin");

app.MapPut("/api/admin/hooks/{name}", (string name, HookAdminUpdateRequest? body, HookRepository hookRepo, RunnerPaths pathsSvc, AuditLogRepository audit, ClaimsPrincipal actor) =>
{
    if (body is null) return Results.BadRequest(new { error = "request body required" });
    var existing = hookRepo.FindByName(name);
    if (existing is null) return Results.NotFound();

    var secret = body.Secret ?? existing.Secret;
    var timeoutSec = body.TimeoutSec ?? existing.TimeoutSec;
    var enabled = body.Enabled ?? existing.Enabled;
    WriteHookConfig(pathsSvc, name, secret, timeoutSec, enabled);
    var hook = hookRepo.UpsertDiscoveredHook(name, existing.HandlerPath, secret, timeoutSec, enabled);
    audit.Record(actor.Identity!.Name!, "hook.update", name, JsonSerializer.Serialize(MapHookForAdmin(existing)), JsonSerializer.Serialize(MapHookForAdmin(hook)));
    return Results.Ok(MapHookForAdmin(hook));
}).RequireAuthorization("Admin");

app.MapDelete("/api/admin/hooks/{name}", (string name, HookRepository hookRepo, AuditLogRepository audit, ClaimsPrincipal actor) =>
{
    var existing = hookRepo.FindByName(name);
    if (existing is null) return Results.NotFound();
    hookRepo.SoftDelete(name);
    audit.Record(actor.Identity!.Name!, "hook.delete", name, JsonSerializer.Serialize(MapHookForAdmin(existing)), null);
    return Results.Ok();
}).RequireAuthorization("Admin");

app.MapGet("/api/admin/hooks/{name}/runs", (string name, HookRepository hookRepo, HookRunRepository hookRunRepo) =>
{
    var hook = hookRepo.FindByName(name);
    if (hook is null) return Results.NotFound();
    return Results.Ok(hookRunRepo.ListRecent(hook.Id, 50));
}).RequireAuthorization("Admin");

// ---- F6: resource descriptions (spec §5 F3a/F6 "リソース"). Lock/wait state is F3a (runtime-only). ----

app.MapGet("/api/admin/resources", (ResourceDefRepository resourceDefs, BuildRepository buildRepo, JobRepository jobRepo, ResourceLockManager resourceLocks) =>
{
    // Union of "described" resources and "currently held" resources - a resource is just a string a
    // job declares (spec §5 F3a "事前定義不要"), so a lock can exist with no description at all.
    var defs = resourceDefs.ListAll().ToDictionary(d => d.Name);
    var held = resourceLocks.Snapshot();
    var waitingJobResources = buildRepo.ListQueued()
        .Where(b => b.Status == BuildStatus.Waiting)
        .Select(b => jobRepo.FindById(b.JobId))
        .Where(j => j is not null)
        .Select(j => { try { return JsonSerializer.Deserialize<List<string>>(j!.Resources) ?? new(); } catch (JsonException) { return new List<string>(); } })
        .ToList();

    var names = defs.Keys.Union(held.Keys).OrderBy(n => n, StringComparer.Ordinal);
    var result = names.Select(name =>
    {
        defs.TryGetValue(name, out var def);
        var holderBuildId = held.TryGetValue(name, out var h) ? (long?)h : null;
        var holderBuild = holderBuildId is null ? null : buildRepo.FindById(holderBuildId.Value);
        var holderJob = holderBuild is null ? null : jobRepo.FindById(holderBuild.JobId);
        return new
        {
            Name = name,
            def?.Description,
            def?.UpdatedAt,
            def?.UpdatedBy,
            HeldByBuildId = holderBuildId,
            HeldByJobName = holderJob?.Name,
            HeldByNumber = holderBuild?.Number,
            WaitingCount = waitingJobResources.Count(r => r.Contains(name)),
        };
    }).ToList();
    return Results.Ok(result);
}).RequireAuthorization("Admin");

app.MapPost("/api/admin/resources", (ResourceDefRequest? body, ResourceDefRepository resourceDefs, AuditLogRepository audit, ClaimsPrincipal actor) =>
{
    if (string.IsNullOrWhiteSpace(body?.Name)) return Results.BadRequest(new { error = "name is required" });
    var actorName = actor.Identity!.Name!;
    var def = resourceDefs.Upsert(body.Name, body.Description, actorName);
    audit.Record(actorName, "resource.upsert", body.Name, null, JsonSerializer.Serialize(def));
    return Results.Ok(def);
}).RequireAuthorization("Admin");

app.MapDelete("/api/admin/resources/{name}", (string name, ResourceDefRepository resourceDefs, AuditLogRepository audit, ClaimsPrincipal actor) =>
{
    if (!resourceDefs.Delete(name)) return Results.NotFound();
    audit.Record(actor.Identity!.Name!, "resource.delete", name, null, null);
    return Results.Ok();
}).RequireAuthorization("Admin");

app.MapPost("/api/admin/resources/{name}/release", (string name, ResourceLockManager resourceLocks, BuildDispatcher dispatcher, BuildRepository buildRepo, JobRepository jobRepo, AuditLogRepository audit, ClaimsPrincipal actor) =>
{
    // Emergency-use escape hatch (spec §5 F3a "異常時用"): does not touch the holding build itself,
    // which may still be running and believes it holds the resource. That's the admin's call to make.
    var holderId = resourceLocks.ForceRelease(name);
    if (holderId is null) return Results.NotFound(new { error = $"resource '{name}' is not currently held" });

    var holderBuild = buildRepo.FindById(holderId.Value);
    var holderJob = holderBuild is null ? null : jobRepo.FindById(holderBuild.JobId);
    dispatcher.Signal();
    audit.Record(actor.Identity!.Name!, "resource.force_release", name,
        JsonSerializer.Serialize(new { heldByBuildId = holderId, jobName = holderJob?.Name, number = holderBuild?.Number }), null);
    return Results.Ok(new { releasedFromBuildId = holderId });
}).RequireAuthorization("Admin");

app.MapGet("/api/jobs", (JobRepository jobRepo, BuildRepository buildRepo) =>
{
    var jobs = jobRepo.ListEnabled().Select(j => new
    {
        j.Name,
        j.Enabled,
        j.RepoUrl,
        j.PipelineSource,
        // Spec §5 F1a: surface each job's declared parameter definitions so a manual trigger can decide
        // whether to prompt with an input form (defaults/required) rather than firing one-click.
        Parameters = ParseJobParameters(j.Parameters),
        LatestBuild = MapBuildSummary(buildRepo.FindLatestByJob(j.Id)),
        RecentBuilds = buildRepo.ListByJob(j.Id, 10).Select(b => new { b.Number, b.Status }),
        // Spec §5 F1b "ジョブ一覧に次回実行時刻を表示" (E2E-019): null for jobs with no cron schedules.
        NextRunAt = ComputeNextRunAt(j),
    });
    return Results.Ok(jobs);
}).RequireAuthorization("Viewer");

app.MapGet("/api/queue", (BuildRepository buildRepo, JobRepository jobRepo, ResourceLockManager resourceLocks) =>
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
            // Spec §5 F3a: "Waiting のビルドには「どのリソースを、どのビルドが塞いでいるか」を表示する".
            BlockedBy = b.Status == BuildStatus.Waiting ? DescribeBlockers(b, job, buildRepo, jobRepo, resourceLocks) : null,
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

app.MapPost("/api/builds/{id:long}/abort", (long id, BuildDispatcher dispatcher) =>
{
    var outcome = dispatcher.Abort(id);
    return outcome switch
    {
        BuildDispatcher.AbortOutcome.NotFound => Results.NotFound(),
        BuildDispatcher.AbortOutcome.AlreadyTerminal => Results.BadRequest(new { error = "build is not queued, waiting, or running" }),
        _ => Results.Ok(),
    };
}).RequireAuthorization("Operator");

app.MapPost("/api/builds/{id:long}/rebuild", (long id, JobTriggerService triggerService) =>
{
    var result = triggerService.Rebuild(id);
    if (!result.Queued && result.Reason is "build-not-found" or "job-not-found-or-disabled")
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
        // Spec §5 F1a: "使用したパラメータは builds.parameters に JSON 保存し、ビルド詳細に表示" - the
        // parameters the build actually ran with, as a {name:value} object ({} when it declared none).
        Parameters = ParseBuildParameters(build.Parameters),
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

/// <summary>Spec §5 F1a: a job's declared parameter definitions (jobs.parameters). Defensive parse so
/// a malformed column yields no parameters rather than failing the whole jobs listing.</summary>
static List<JobParameterDef> ParseJobParameters(string json)
{
    try { return JsonSerializer.Deserialize<List<JobParameterDef>>(json) ?? new(); }
    catch (JsonException) { return new(); }
}

/// <summary>Spec §5 F1a: the parameters a build actually ran with (builds.parameters, a {name:value}
/// JSON object). Empty object when the build declared none or the column is malformed.</summary>
static Dictionary<string, string> ParseBuildParameters(string json)
{
    try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(); }
    catch (JsonException) { return new(); }
}

/// <summary>Spec §5 F3a: for a Waiting build, which of its declared resources are held and by which
/// other build. Null (rather than an empty list) when nothing is actually contended, e.g. a build
/// that just transitioned to Waiting this instant and hasn't lost the race to anyone yet.</summary>
static List<object>? DescribeBlockers(BuildRecord waiting, JobRecord? job, BuildRepository buildRepo, JobRepository jobRepo, ResourceLockManager resourceLocks)
{
    if (job is null)
    {
        return null;
    }
    List<string>? resources;
    try
    {
        resources = JsonSerializer.Deserialize<List<string>>(job.Resources);
    }
    catch (JsonException)
    {
        return null;
    }
    if (resources is null || resources.Count == 0)
    {
        return null;
    }

    var blockers = new List<object>();
    foreach (var resource in resources)
    {
        var holderId = resourceLocks.HolderOf(resource);
        if (holderId is null || holderId == waiting.Id)
        {
            continue;
        }
        var holderBuild = buildRepo.FindById(holderId.Value);
        var holderJob = holderBuild is null ? null : jobRepo.FindById(holderBuild.JobId);
        blockers.Add(new { Resource = resource, BuildId = holderId, JobName = holderJob?.Name, Number = holderBuild?.Number });
    }
    return blockers.Count > 0 ? blockers : null;
}

static string? ValidateJobRequest(JobAdminRequest body)
{
    if (body.PipelineSource is not (null or "server" or "repo"))
    {
        return "pipelineSource must be 'server' or 'repo'";
    }
    if (body.PipelineSource == "repo" && string.IsNullOrWhiteSpace(body.RepoUrl))
    {
        return "repoUrl is required when pipelineSource is 'repo'";
    }
    if (body.QueuePolicy is not (null or "queue" or "replace"))
    {
        return "queuePolicy must be 'queue' or 'replace'";
    }
    // Spec §5 F1b / test-spec TMR-005: reject a malformed cron expression at config time rather than
    // letting it silently never fire (CronScheduler.CheckOnce just skips schedules it can't parse). A
    // job can declare several schedules (TMR-002), so every one of them is checked.
    if (body.CronSchedules is not null)
    {
        foreach (var expr in body.CronSchedules)
        {
            if (!CronScheduler.TryParse(expr, out _, out var cronError))
            {
                return $"invalid cron expression '{expr}': {cronError}";
            }
        }
    }
    return null;
}

/// <summary>Spec §5 F1b "次回発火時刻": the soonest upcoming occurrence across all of a job's cron
/// schedules, or null if it has none / none currently parse. Malformed schedules are skipped here
/// rather than thrown - job create/update already rejects those (TMR-005), but a job.json hand-edited
/// on disk could still contain one, and this must not break the jobs list over it.</summary>
static DateTimeOffset? ComputeNextRunAt(JobRecord job)
{
    List<string>? schedules;
    try
    {
        schedules = JsonSerializer.Deserialize<List<string>>(job.CronSchedules);
    }
    catch (JsonException)
    {
        return null;
    }
    if (schedules is null || schedules.Count == 0)
    {
        return null;
    }

    DateTimeOffset? earliest = null;
    var now = DateTimeOffset.Now;
    foreach (var expr in schedules)
    {
        if (!CronScheduler.TryParse(expr, out var cron, out _))
        {
            continue;
        }
        var next = cron.GetNextOccurrence(now, TimeZoneInfo.Local);
        if (next is { } occurrence && (earliest is null || occurrence < earliest))
        {
            earliest = occurrence;
        }
    }
    return earliest;
}

/// <summary>Applies a job create/update to both the DB and jobs/&lt;name&gt;/job.json, so the next
/// JobScanner rescan (process restart) reproduces this state instead of clobbering it.</summary>
static JobRecord ApplyJobConfig(RunnerPaths paths, JobRepository jobRepo, string name, JobAdminRequest body)
{
    var parameters = (body.Parameters ?? new List<JobParameterDef>())
        .Where(p => !ParameterResolver.IsReservedName(p.Name))
        .ToList();
    var input = new JobConfigInput(
        Name: name,
        RepoUrl: body.RepoUrl,
        WorkspacePath: body.WorkspacePath,
        PipelineSource: body.PipelineSource ?? "server",
        PipelinePath: body.PipelinePath ?? "pipeline.cipipe",
        ParametersJson: JsonSerializer.Serialize(parameters),
        CronSchedulesJson: JsonSerializer.Serialize(body.CronSchedules ?? new List<string>()),
        PollingBranchesJson: body.PollingBranches is null ? null : JsonSerializer.Serialize(body.PollingBranches),
        ResourcesJson: JsonSerializer.Serialize(body.Resources ?? new List<string>()),
        QueuePolicy: body.QueuePolicy ?? "replace",
        TimeoutMinutes: body.TimeoutMinutes,
        Retention: body.Retention,
        ShellPath: body.ShellPath,
        Enabled: body.Enabled ?? true);

    var job = jobRepo.UpsertConfiguredJob(input);

    Directory.CreateDirectory(paths.JobDir(name));
    File.WriteAllText(paths.JobConfigPath(name), JsonSerializer.Serialize(ToExportDto(job), new JsonSerializerOptions { WriteIndented = true }));

    if (job.PipelineSource == "server" && !File.Exists(paths.JobPipelinePath(name)))
    {
        File.WriteAllText(paths.JobPipelinePath(name), PipelineTemplate);
    }

    return job;
}

static object ToExportDto(JobRecord job) => new
{
    name = job.Name,
    repoUrl = job.RepoUrl,
    workspacePath = job.WorkspacePath,
    pipelineSource = job.PipelineSource,
    pipelinePath = job.PipelinePath,
    parameters = JsonSerializer.Deserialize<List<JobParameterDef>>(job.Parameters),
    cronSchedules = JsonSerializer.Deserialize<List<string>>(job.CronSchedules),
    pollingBranches = job.PollingBranches is null ? null : JsonSerializer.Deserialize<List<string>>(job.PollingBranches),
    resources = JsonSerializer.Deserialize<List<string>>(job.Resources),
    queuePolicy = job.QueuePolicy,
    timeoutMinutes = job.TimeoutMinutes,
    retention = job.Retention,
    shellPath = job.ShellPath,
    enabled = job.Enabled,
};

/// <summary>Never echoes the raw HMAC secret back through the API (spec §5 F6 "シークレット(値は伏せる)") -
/// only whether one is set. The value stays usable server-side for webhook verification (WebhookReceiver
/// reads HookRecord.Secret directly), it's just never re-served once written.</summary>
static object MapHookForAdmin(HookRecord h) => new
{
    h.Id,
    h.Name,
    HasSecret = !string.IsNullOrEmpty(h.Secret),
    h.HandlerPath,
    h.TimeoutSec,
    h.Enabled,
    h.Deleted,
    h.CreatedAt,
};

static void WriteHookConfig(RunnerPaths paths, string name, string? secret, int timeoutSec, bool enabled)
{
    Directory.CreateDirectory(paths.HooksDir);
    File.WriteAllText(paths.HookConfigPath(name), JsonSerializer.Serialize(new { secret, timeoutSec, enabled }, new JsonSerializerOptions { WriteIndented = true }));
}

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

sealed class SettingsUpdateRequest
{
    public int? Executors { get; set; }
    public int? DefaultTimeoutMinutes { get; set; }
    public int? DefaultRetention { get; set; }
    public int? PollingIntervalSec { get; set; }
    public string? TestResultMode { get; set; }
    public int? HandlerConcurrency { get; set; }
}

/// <summary>Body shape for job create/update/import. Name is required for create/import and ignored on update (the route provides it).</summary>
sealed class JobAdminRequest
{
    public string? Name { get; set; }
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

sealed class HookAdminCreateRequest
{
    public string? Name { get; set; }
    public string? Secret { get; set; }
    public int? TimeoutSec { get; set; }
    public bool? Enabled { get; set; }
}

sealed class HookAdminUpdateRequest
{
    public string? Secret { get; set; }
    public int? TimeoutSec { get; set; }
    public bool? Enabled { get; set; }
}

sealed class ResourceDefRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
}
