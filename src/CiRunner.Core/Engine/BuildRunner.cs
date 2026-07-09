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
    private readonly LiveLogHub _logHub;
    private readonly GlobalEventHub _eventHub;
    private readonly string _bootstrapScriptPath;
    private readonly string _serverUrl;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(60);

    public BuildRunner(
        RunnerPaths paths,
        BuildRepository buildRepo,
        LiveLogHub logHub,
        GlobalEventHub eventHub,
        string bootstrapScriptPath,
        string serverUrl)
    {
        _paths = paths;
        _buildRepo = buildRepo;
        _logHub = logHub;
        _eventHub = eventHub;
        _bootstrapScriptPath = bootstrapScriptPath;
        _serverUrl = serverUrl;
    }

    public async Task RunAsync(JobRecord job, BuildRecord build, CancellationToken ct)
    {
        var logPath = _paths.BuildLogPath(job.Name, build.Number);
        _logHub.OpenForWriting(build.Id, logPath);

        var pipelinePath = _paths.JobPipelinePath(job.Name);
        var workspaceDir = string.IsNullOrEmpty(job.WorkspacePath) ? _paths.JobWorkspaceDir(job.Name) : job.WorkspacePath;
        Directory.CreateDirectory(workspaceDir);

        var controlFilePath = Path.Combine(_paths.ControlFilesDir, $"{build.Id}.jsonl");
        File.WriteAllText(controlFilePath, string.Empty, new UTF8Encoding(false));

        var resultDir = Path.Combine(_paths.ResultsDir, build.Id.ToString());
        var artifactDir = Path.Combine(_paths.ArtifactsDir, build.Id.ToString());
        Directory.CreateDirectory(resultDir);
        Directory.CreateDirectory(artifactDir);

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
        }

        FinishBuild(build.Id, finalStatus);
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
            // "start", "junit", "artifact" and any future event types are either informational only for
            // M1 or handled by later milestones (F4 test-result / artifact ingestion); unknown ev values
            // are ignored per DSL spec §4.3 forward-compatibility rule.
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
