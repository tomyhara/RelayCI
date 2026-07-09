using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CiRunner.Host.Tests.Support;

public sealed record TestLocalUser(string Username, string Password, string Role);

/// <summary>
/// Launches the real CiRunner.Host.dll as a subprocess bound to a real Kestrel listener on a random
/// port (ci-runner-test-spec.md §3 "L3: ... 実 Kestrel をランダムポートで起動"), mirroring how L3
/// DSL tests launch a real powershell.exe rather than mocking it out.
/// </summary>
public sealed class HostProcess : IAsyncDisposable
{
    public HttpClient Client { get; }
    public string Root { get; }
    public int Port { get; }

    private Process? _process;
    private readonly bool _deleteRootOnDispose;

    private HostProcess(string root, int port, bool deleteRootOnDispose)
    {
        Root = root;
        Port = port;
        _deleteRootOnDispose = deleteRootOnDispose;
        Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
    }

    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static string WriteConfig(
        string root,
        int port,
        IEnumerable<TestLocalUser> localUsers,
        IEnumerable<string>? initialAdmins = null,
        string defaultRole = "viewer",
        int sessionHours = 1)
    {
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "config.json");
        var json = JsonSerializer.Serialize(new
        {
            port,
            bind = "127.0.0.1",
            auth = new
            {
                initialAdmins = (initialAdmins ?? Array.Empty<string>()).ToArray(),
                defaultRole,
                sessionHours,
                localUsers = localUsers.Select(u => new { username = u.Username, password = u.Password, role = u.Role }).ToArray(),
            },
        });
        File.WriteAllText(configPath, json);
        return configPath;
    }

    /// <summary>Starts a fresh temp root + config with the given users, then launches the host.</summary>
    public static async Task<HostProcess> StartAsync(
        IEnumerable<TestLocalUser> localUsers,
        IEnumerable<string>? initialAdmins = null,
        string defaultRole = "viewer",
        int sessionHours = 1,
        string? hostDllPath = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ci-host-{Guid.NewGuid()}");
        var port = GetFreePort();
        var configPath = WriteConfig(root, port, localUsers, initialAdmins, defaultRole, sessionHours);
        return await StartAsync(root, port, configPath, deleteRootOnDispose: true, hostDllPath);
    }

    /// <summary>Starts against an existing root/config (used to simulate a restart against the same DB).</summary>
    public static async Task<HostProcess> StartAsync(string root, int port, string configPath, bool deleteRootOnDispose = true, string? hostDllPath = null)
    {
        var dllPath = hostDllPath ?? Path.Combine(AppContext.BaseDirectory, "CiRunner.Host.dll");
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException($"CiRunner.Host.dll not found at {dllPath}; expected the ProjectReference to copy it next to the test assembly.", dllPath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = ResolveDotnetExe(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root,
        };
        psi.ArgumentList.Add(dllPath);
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(root);
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(configPath);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start CiRunner.Host process");
        var host = new HostProcess(root, port, deleteRootOnDispose);
        host._process = process;
        await host.WaitUntilReadyAsync();
        return host;
    }

    private async Task WaitUntilReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            if (_process!.HasExited)
            {
                var stderr = await _process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"host process exited early (code {_process.ExitCode}): {stderr}");
            }
            try
            {
                using var res = await Client.GetAsync("/api/me");
                if (res.IsSuccessStatusCode) return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
            await Task.Delay(150);
        }
        throw new TimeoutException("CiRunner.Host did not become ready in time", last);
    }

    /// <summary>Stops the process without deleting the root directory, so a follow-up StartAsync can reuse it.</summary>
    public async Task StopAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
    }

    private static string ResolveDotnetExe()
    {
        var processPath = Environment.ProcessPath;
        if (processPath is not null && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (root is not null)
        {
            var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate)) return candidate;
        }
        return "dotnet";
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        _process?.Dispose();
        if (_deleteRootOnDispose)
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
        await Task.CompletedTask;
    }
}
