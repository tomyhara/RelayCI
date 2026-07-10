# RelayCI

A self-contained CI runner for Windows that needs **no administrator rights** to install, run, or
update. It covers the small slice of CI functionality most teams actually use — webhook-triggered
builds, a stage-based pipeline DSL, live logs, test-result aggregation, and a web UI — as a single
`.exe` with no external runtime, no Node/npm, and no internet dependency at runtime.

## Why

Standard CI tools (Jenkins and friends) assume you can install a JVM, a Windows service, or admin-level
tooling on the host. RelayCI targets environments where that's off the table: it deploys by unzipping
an archive, updates by replacing the `.exe`, and runs entirely under a standard user account.

## Features

| Area | What it does |
|---|---|
| Webhook receiver | Accepts arbitrary webhooks; a user-written PowerShell handler script inspects the payload and decides whether/how to start a job |
| Parameterized builds | Jobs declare typed string parameters, passed in from webhooks, the UI, cron, or the API |
| Cron triggers | Timer-based job triggers on a standard cron expression, with next-run preview |
| Pipeline DSL | PowerShell-native `.cipipe` files: `Stage`, `PostStage` (`Success`/`Failure`/`Always`), `Exec`, JUnit/artifact registration |
| Execution engine | Git checkout, child-process execution, stage-by-stage success/failure tracking, workspace management |
| Test results | Runs arbitrary commands and ingests JUnit XML output |
| Web UI | Job list, build history, live log streaming (SSE), manual/parameterized triggers |
| Admin UI | Job, role, and resource configuration without editing files by hand |

## Architecture

Single process, single `.exe` (ASP.NET Core / Kestrel):

```
┌────────────────────────────────────────────────────┐
│  CiRunner.Host (Kestrel)                            │
│                                                      │
│   Web UI + REST API + SSE ──┐                       │
│   Webhook endpoint  ────────┼──▶ Job Queue ──▶ Executors ──▶ PowerShell child process
│                              │                        (bootstrap.ps1 + .cipipe)
│   SQLite (WAL) ◀─────────────┘                       │
└────────────────────────────────────────────────────┘
```

- **HTTP**: Kestrel directly — no `netsh http add urlacl`, no admin rights needed.
- **UI**: a single HTML page (inline CSS/JS) embedded in the exe. No CDN, no external assets.
- **State**: SQLite (WAL mode) for job/build metadata; build logs are plain text files on disk.
- **Execution**: pipelines are PowerShell scripts (`.cipipe`) run via a small `bootstrap.ps1`, with
  stdout/stderr streamed live to the log file and to the UI over SSE.

## Requirements

- Windows 10/11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) for building; the published app is
  self-contained and needs no runtime installed
- `git` on `PATH` (or a portable Git pointed to via config)

## Getting started

```powershell
git clone git@github.com:tomyhara/RelayCI.git
cd RelayCI
dotnet build CiRunner.sln
dotnet run --project src/CiRunner.Host -- --port 8080 --root .
```

Command-line flags (all optional): `--config <path>` (default `config.json`), `--root <dir>` (default
current directory — holds `jobs/`, `hooks/`, `workspaces/`, `logs/`, `data/`), `--port`, `--bind`.

Minimal `config.json`:

```json
{
  "bind": "0.0.0.0",
  "port": 8080,
  "git": { "exePath": "git" },
  "ghes": { "baseUrl": "https://your-ghes-host/api/v3", "pat": "..." },
  "auth": {
    "ldap": { "server": "ldaps://dc.example.local", "searchBase": "DC=example,DC=local" },
    "initialAdmins": ["yourusername"]
  }
}
```

A job's pipeline (`jobs/<job-name>/pipeline.cipipe`):

```powershell
Stage "Build" {
    Exec { dotnet build MySolution.sln -c Release }
}

Stage "Test" {
    Exec { dotnet test --logger "junit;LogFilePath=results.xml" }
    Register-JUnit "**/results.xml"
}

PostStage -When Always {
    Register-Artifact "bin/Release/**"
}
```

A webhook handler (`hooks/<hook-name>.cipipe`) is a restricted-surface script — `Get-HookPayload`,
`Get-HookHeader`, `Start-CiJob`, `Exec` only:

```powershell
$payload = Get-HookPayload | ConvertFrom-Json
if ($payload.ref -eq "refs/heads/main") {
    Start-CiJob -Name "my-job" -Parameters @{ ref = $payload.ref }
}
```

Publishing a self-contained single-file exe:

```powershell
dotnet publish src/CiRunner.Host -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Project structure

```
src/
  CiRunner.Core/     domain logic: engine, pipeline parsing, auth, config, data access
  CiRunner.Host/     ASP.NET Core host, web UI, DSL PowerShell module (psmodule/)
tests/
  CiRunner.Core.Tests/   L2 unit tests (xUnit) — parsers, matchers, queue logic
  CiRunner.Host.Tests/   L3 integration tests (xUnit) — real Kestrel host + SQLite + local bare git repo
  CiRunner.E2E.Tests/    L4 end-to-end tests (Playwright for .NET) — browser through to build results
  CiRunner.Dsl.Tests/    L1 pipeline DSL tests (Pester 5) — CiRunner.psm1 / bootstrap.ps1
```

## Testing

| Level | Scope | How to run |
|---|---|---|
| L1 | DSL module (`CiRunner.psm1`, `bootstrap.ps1`) | `pwsh tests/CiRunner.Dsl.Tests/RunTests.ps1` (requires Pester ≥ 5) |
| L2 | Core unit tests | `dotnet test tests/CiRunner.Core.Tests` |
| L3 | Host integration (real Kestrel + SQLite + local bare git repo) | `dotnet test tests/CiRunner.Host.Tests` |
| L4 | End-to-end (browser → build → results) | `dotnet test tests/CiRunner.E2E.Tests` |

Tests must be built/run in **Debug** configuration — `auth.localUsers` (the LDAP test double used by
L3/L4 fixtures) is rejected at startup outside Debug builds by design.

The full `CiRunner.E2E.Tests` suite can be flaky under parallel execution/CPU contention (browser +
real host + child processes per test class); if a run fails, re-run the affected class filtered
(`dotnet test --filter "FullyQualifiedName~<ClassName>"`) before treating it as a real regression.

Run everything:

```powershell
dotnet build CiRunner.sln
dotnet test tests/CiRunner.Core.Tests
dotnet test tests/CiRunner.Host.Tests
pwsh tests/CiRunner.E2E.Tests/bin/Debug/net8.0/playwright.ps1 install chromium
dotnet test tests/CiRunner.E2E.Tests
pwsh tests/CiRunner.Dsl.Tests/RunTests.ps1
```

## Non-goals

Distributed/multi-agent execution, a plugin system, general user/permission management beyond
role-gated LDAP auth, an artifact repository (artifacts are served from disk), and built-in
notifications (send them from a pipeline script instead).
