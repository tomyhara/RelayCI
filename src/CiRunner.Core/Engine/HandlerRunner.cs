using System.Diagnostics;
using System.Text;
using CiRunner.Core.Data;
using CiRunner.Core.Models;
using CiRunner.Core.Paths;

namespace CiRunner.Core.Engine;

/// <summary>
/// Executes a webhook handler script (DSL spec §10): bootstrap.ps1 -Mode Handler, no control file
/// or Stage lifecycle. Runs in a dedicated concurrency slot separate from build Executors
/// (spec §5 F1 "Executor を消費しない専用スロット").
/// </summary>
public sealed class HandlerRunner
{
    private readonly RunnerPaths _paths;
    private readonly HookRunRepository _hookRunRepo;
    private readonly string _bootstrapScriptPath;
    private readonly string _serverUrl;

    public HandlerRunner(RunnerPaths paths, HookRunRepository hookRunRepo, string bootstrapScriptPath, string serverUrl)
    {
        _paths = paths;
        _hookRunRepo = hookRunRepo;
        _bootstrapScriptPath = bootstrapScriptPath;
        _serverUrl = serverUrl;
    }

    public async Task RunAsync(HookRecord hook, long hookRunId, string payloadPath, string headersPath, string? eventName, string? deliveryId, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _paths.HooksDir,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(_bootstrapScriptPath);
        psi.ArgumentList.Add("-PipelinePath");
        psi.ArgumentList.Add(hook.HandlerPath);
        psi.ArgumentList.Add("-Mode");
        psi.ArgumentList.Add("Handler");

        psi.Environment["CI_HOOK_NAME"] = hook.Name;
        psi.Environment["CI_HOOK_EVENT"] = eventName ?? "";
        psi.Environment["CI_HOOK_DELIVERY"] = deliveryId ?? "";
        psi.Environment["CI_HOOK_PAYLOAD"] = payloadPath;
        psi.Environment["CI_HOOK_HEADERS"] = headersPath;
        psi.Environment["CI_HANDLER_SCRIPTS"] = _paths.HookScriptsDir;
        psi.Environment["CI_SERVER_URL"] = _serverUrl;
        // Implementation extension (not in DSL spec's env var table): lets Start-CiJob attribute
        // triggered builds back to this hook_run for the "why did it fire" history view.
        psi.Environment["CI_HOOK_RUN_ID"] = hookRunId.ToString();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var log = new StringBuilder();
        var logLock = new object();

        void AppendLog(string line)
        {
            lock (logLock)
            {
                log.Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("] ").AppendLine(line);
            }
        }

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) AppendLog(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) AppendLog(e.Data); };

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(hook.TimeoutSec));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // process may have already exited between the cancellation and the kill attempt.
            }
        }

        var status = timedOut
            ? HookRunStatus.Timeout
            : process.ExitCode == 0 ? HookRunStatus.Success : HookRunStatus.Failed;

        var logPath = _paths.HookRunLogPath(hookRunId);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(logPath, log.ToString(), new UTF8Encoding(false), CancellationToken.None);

        _hookRunRepo.Complete(hookRunId, status, logPath);
    }
}
