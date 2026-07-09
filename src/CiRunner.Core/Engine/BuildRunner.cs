using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CiRunner.Core.Data;
using CiRunner.Core.Models;
using CiRunner.Core.Paths;
using CiRunner.Core.Pipeline;

namespace CiRunner.Core.Engine;

/// <summary>
/// Executes a single build: launches <c>bootstrap.ps1</c> per DSL spec §1-2, streams stdout/stderr to the
/// per-build log file (§8), tails the control file for structured events (§4), and persists build_steps.
/// </summary>
public sealed class BuildRunner
{
    private readonly RunnerPaths _paths;
    private readonly BuildRepository _buildRepo;
    private readonly TestResultRepository _testResultRepo;
    private readonly ArtifactRepository _artifactRepo;
    private readonly SettingsRepository _settings;
    private readonly LiveLogHub _logHub;
    private readonly GlobalEventHub _eventHub;
    private readonly string _bootstrapScriptPath;
    private readonly string _serverUrl;
    private readonly string _gitExePath;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(60);

    public BuildRunner(
        RunnerPaths paths,
        BuildRepository buildRepo,
        TestResultRepository testResultRepo,
        ArtifactRepository artifactRepo,
        SettingsRepository settings,
        LiveLogHub logHub,
        GlobalEventHub eventHub,
        string bootstrapScriptPath,
        string serverUrl,
        string gitExePath = "git")
    {
        _paths = paths;
        _buildRepo = buildRepo;
        _testResultRepo = testResultRepo;
        _artifactRepo = artifactRepo;
        _settings = settings;
        _logHub = logHub;
        _eventHub = eventHub;
        _bootstrapScriptPath = bootstrapScriptPath;
        _serverUrl = serverUrl;
        _gitExePath = gitExePath;
    }

    public async Task RunAsync(JobRecord job, BuildRecord build, CancellationToken ct)
    {
        var logPath = _paths.BuildLogPath(job.Name, build.Number);
        _logHub.OpenForWriting(build.Id, logPath);

        var workspaceDir = string.IsNullOrEmpty(job.WorkspacePath) ? _paths.JobWorkspaceDir(job.Name) : job.WorkspacePath;
        Directory.CreateDirectory(workspaceDir);

        var controlFilePath = Path.Combine(_paths.ControlFilesDir, $"{build.Id}.jsonl");
        File.WriteAllText(controlFilePath, string.Empty, new UTF8Encoding(false));

        var resultDir = Path.Combine(_paths.ResultsDir, build.Id.ToString());
        var artifactDir = Path.Combine(_paths.ArtifactsDir, build.Id.ToString());
        Directory.CreateDirectory(resultDir);
        Directory.CreateDirectory(artifactDir);

        // Repository ジョブ (spec §5 F3): clone/fetch + checkout <SHA|branch|origin/HEAD> + clean -fdx.
        // Repo-less jobs skip this entirely and just use the (possibly fixed) workspace as-is.
        if (!string.IsNullOrEmpty(job.RepoUrl))
        {
            var checkedOut = await CheckoutRepoAsync(job, build, workspaceDir, ct);
            if (!checkedOut)
            {
                FinishBuild(build.Id, BuildStatus.Failed);
                return;
            }
        }

        var pipelinePath = job.PipelineSource == "repo"
            ? Path.Combine(workspaceDir, (job.PipelinePath ?? ".ci/pipeline.cipipe").Replace('/', Path.DirectorySeparatorChar))
            : _paths.JobPipelinePath(job.Name);

        if (!File.Exists(pipelinePath))
        {
            WriteLogLine(build.Id, $"pipeline definition not found: {pipelinePath}");
            FinishBuild(build.Id, BuildStatus.Failed);
            return;
        }

        var shellPath = string.IsNullOrEmpty(job.ShellPath) ? "powershell.exe" : job.ShellPath;
        var psi = new ProcessStartInfo
        {
            FileName = shellPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workspaceDir,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(_bootstrapScriptPath);
        psi.ArgumentList.Add("-PipelinePath");
        psi.ArgumentList.Add(pipelinePath);

        psi.Environment["CI_JOB_NAME"] = job.Name;
        psi.Environment["CI_BUILD_NUMBER"] = build.Number.ToString();
        psi.Environment["CI_BUILD_ID"] = build.Id.ToString();
        psi.Environment["CI_TRIGGER"] = build.Trigger;
        psi.Environment["CI_WORKSPACE"] = workspaceDir;
        psi.Environment["CI_CONTROL_FILE"] = controlFilePath;
        psi.Environment["CI_RESULT_DIR"] = resultDir;
        psi.Environment["CI_ARTIFACT_DIR"] = artifactDir;
        psi.Environment["CI_SERVER_URL"] = _serverUrl;
        psi.Environment["CI_BUILD_URL"] = $"{_serverUrl}/jobs/{Uri.EscapeDataString(job.Name)}/builds/{build.Number}";
        if (!string.IsNullOrEmpty(build.CommitSha))
        {
            psi.Environment["CI_COMMIT_SHA"] = build.CommitSha;
        }
        if (!string.IsNullOrEmpty(build.Branch))
        {
            psi.Environment["CI_BRANCH"] = build.Branch;
        }
        ApplyDeclaredParameters(psi, build.Parameters);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var timeout = job.TimeoutMinutes is > 0 ? TimeSpan.FromMinutes(job.TimeoutMinutes.Value) : DefaultTimeout;
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        process.Start();

        var stdoutTask = PumpStreamAsync(process.StandardOutput, build.Id);
        var stderrTask = PumpStreamAsync(process.StandardError, build.Id);
        var controlTask = ConsumeControlEventsAsync(controlFilePath, () => !process.HasExited, build.Id);

        var killedByTimeoutOrAbort = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            killedByTimeoutOrAbort = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // process may have already exited between the cancellation and the kill attempt.
            }
        }

        await Task.WhenAll(stdoutTask, stderrTask, controlTask);

        string finalStatus;
        if (killedByTimeoutOrAbort)
        {
            finalStatus = BuildStatus.Aborted;
            WriteLogLine(build.Id, timeoutCts.IsCancellationRequested ? "build timed out; process tree killed" : "build aborted; process tree killed");
        }
        else
        {
            finalStatus = process.ExitCode == 0 ? BuildStatus.Success : BuildStatus.Failed;

            // Strict mode (spec §5 F4, default): a build that exits 0 is still Failed if any
            // ingested JUnit test case failed or errored. exit-code-only trusts the process exit
            // code alone. Only applies when the build would otherwise be Success - already-failed
            // builds stay failed regardless.
            if (finalStatus == BuildStatus.Success
                && _settings.GetString("testResultMode", "strict") == "strict"
                && _testResultRepo.HasFailures(build.Id))
            {
                finalStatus = BuildStatus.Failed;
                WriteLogLine(build.Id, "build marked Failed: one or more JUnit test cases failed (strict mode)");
            }
        }

        FinishBuild(build.Id, finalStatus);
    }

    /// <summary>
    /// Clone (first use) or fetch + checkout &lt;SHA|branch|origin/HEAD&gt; + clean -fdx (spec §5 F3).
    /// Mutates and persists build.CommitSha to the SHA actually checked out, so env var injection
    /// and the UI reflect what really ran even when the caller only knew a branch name.
    /// </summary>
    private async Task<bool> CheckoutRepoAsync(JobRecord job, BuildRecord build, string workspaceDir, CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(Path.Combine(workspaceDir, ".git")))
            {
                WriteLogLine(build.Id, $"$ git clone {job.RepoUrl} .");
                if (!await RunGitAsync(workspaceDir, build.Id, ct, "clone", job.RepoUrl!, "."))
                {
                    return false;
                }
            }
            else
            {
                WriteLogLine(build.Id, "$ git fetch --prune origin");
                if (!await RunGitAsync(workspaceDir, build.Id, ct, "fetch", "--prune", "origin"))
                {
                    return false;
                }
            }

            string checkoutTarget;
            if (!string.IsNullOrEmpty(build.CommitSha))
            {
                checkoutTarget = build.CommitSha;
            }
            else if (!string.IsNullOrEmpty(build.Branch))
            {
                checkoutTarget = $"origin/{build.Branch}";
            }
            else
            {
                WriteLogLine(build.Id, "$ git remote set-head origin --auto");
                await RunGitAsync(workspaceDir, build.Id, ct, "remote", "set-head", "origin", "--auto");
                checkoutTarget = "origin/HEAD";
            }

            WriteLogLine(build.Id, $"$ git checkout --force {checkoutTarget}");
            if (!await RunGitAsync(workspaceDir, build.Id, ct, "checkout", "--force", checkoutTarget))
            {
                return false;
            }

            WriteLogLine(build.Id, "$ git clean -fdx");
            await RunGitAsync(workspaceDir, build.Id, ct, "clean", "-fdx");

            var resolvedSha = await CaptureGitOutputAsync(workspaceDir, ct, "rev-parse", "HEAD");
            if (resolvedSha is not null)
            {
                build.CommitSha = resolvedSha.Trim();
                _buildRepo.SetCommitInfo(build.Id, build.CommitSha, build.Branch);
            }
            return true;
        }
        catch (Exception ex)
        {
            WriteLogLine(build.Id, $"git checkout failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> RunGitAsync(string workDir, long buildId, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _gitExePath,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {_gitExePath}");
        var stdoutTask = PumpStreamAsync(process.StandardOutput, buildId);
        var stderrTask = PumpStreamAsync(process.StandardError, buildId);
        await process.WaitForExitAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        return process.ExitCode == 0;
    }

    private async Task<string?> CaptureGitOutputAsync(string workDir, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _gitExePath,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {_gitExePath}");
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0 ? output : null;
    }

    private static void ApplyDeclaredParameters(ProcessStartInfo psi, string parametersJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    psi.Environment[prop.Name] = prop.Value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // parameters column defaults to "{}"; malformed content is ignored rather than failing the build.
        }
    }

    private async Task PumpStreamAsync(StreamReader reader, long buildId)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            WriteLogLine(buildId, line);
        }
    }

    private void WriteLogLine(long buildId, string line)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        _logHub.AppendLine(buildId, $"[{timestamp}] {line}");
    }

    private async Task ConsumeControlEventsAsync(string controlFilePath, Func<bool> isProcessRunning, long buildId)
    {
        var tailer = new ControlFileTailer();
        await foreach (var evt in tailer.TailAsync(controlFilePath, isProcessRunning))
        {
            HandleControlEvent(buildId, evt);
        }
    }

    private void HandleControlEvent(long buildId, ControlFileEvent evt)
    {
        switch (evt.Ev)
        {
            case "stage-start":
            {
                var seq = evt.GetInt("seq") ?? 0;
                var name = evt.GetString("name") ?? $"stage-{seq}";
                var post = evt.GetString("post");
                _buildRepo.UpsertStepStart(buildId, seq, name, post);
                _buildRepo.UpdateStepLogOffsets(buildId, seq, _logHub.CurrentLength(buildId), null);
                PublishBuildEvent(buildId, "stage-start", new { seq, name, post });
                break;
            }
            case "stage-end":
            {
                var seq = evt.GetInt("seq") ?? 0;
                var status = evt.GetString("status") ?? StepStatus.Failed;
                var error = evt.GetString("error");
                _buildRepo.UpdateStepEnd(buildId, seq, status, error);
                _buildRepo.UpdateStepLogOffsets(buildId, seq, null, _logHub.CurrentLength(buildId));
                PublishBuildEvent(buildId, "stage-end", new { seq, status, error });
                break;
            }
            case "note":
            {
                var text = evt.GetString("text") ?? "";
                if (text.Length > 200)
                {
                    text = text[..200];
                }
                _buildRepo.SetNote(buildId, text);
                PublishBuildEvent(buildId, "note", new { text });
                break;
            }
            case "warning":
            {
                var message = evt.GetString("message") ?? "";
                WriteLogLine(buildId, $"WARNING: {message}");
                break;
            }
            case "error":
            {
                var message = evt.GetString("message") ?? "";
                WriteLogLine(buildId, $"ERROR: {message}");
                break;
            }
            case "junit":
            {
                HandleJUnitEvent(buildId, evt);
                break;
            }
            case "artifact":
            {
                HandleArtifactEvent(buildId, evt);
                break;
            }
            // "start" and any future event types are ignored per DSL spec §4.3 forward-compatibility rule.
        }
    }

    private void HandleJUnitEvent(long buildId, ControlFileEvent evt)
    {
        var files = evt.GetArray("files");
        if (files is null)
        {
            return;
        }

        var resultDir = Path.Combine(_paths.ResultsDir, buildId.ToString());
        foreach (var fileEl in files.Value.EnumerateArray())
        {
            var relPath = fileEl.GetString();
            if (string.IsNullOrEmpty(relPath))
            {
                continue;
            }

            var fullPath = Path.Combine(resultDir, relPath);
            if (JUnitXmlParser.TryParse(fullPath, out var results, out var error))
            {
                _testResultRepo.InsertMany(buildId, results);
            }
            else
            {
                WriteLogLine(buildId, $"WARNING: could not parse JUnit XML '{relPath}': {error}");
            }
        }
    }

    private void HandleArtifactEvent(long buildId, ControlFileEvent evt)
    {
        var files = evt.GetArray("files");
        if (files is null)
        {
            return;
        }

        foreach (var fileEl in files.Value.EnumerateArray())
        {
            if (fileEl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var path = fileEl.TryGetProperty("path", out var p) ? p.GetString() : null;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }
            long? size = fileEl.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null;
            _artifactRepo.Insert(buildId, path, size);
        }
    }

    private void PublishBuildEvent(long buildId, string type, object payload)
    {
        var json = JsonSerializer.Serialize(new { buildId, type, payload });
        _eventHub.Publish(json);
    }

    private void FinishBuild(long buildId, string status)
    {
        _buildRepo.UpdateStatus(buildId, status, finishedAt: DateTimeOffset.Now.ToString("o"));
        _logHub.Complete(buildId);
        PublishBuildEvent(buildId, "build-finished", new { status });
    }
}
