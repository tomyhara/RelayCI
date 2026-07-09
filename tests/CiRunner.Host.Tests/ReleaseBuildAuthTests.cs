using System.Diagnostics;
using CiRunner.Host.Tests.Support;

namespace CiRunner.Host.Tests;

/// <summary>
/// AUTH-010: a Release build must refuse to start at all if auth.localUsers is set in config.json -
/// it's a Debug-only LDAP test double (spec §9 addendum, ci-runner-test-spec.md §9.1). This compiles
/// a real Release build of CiRunner.Host, so it is slower than the other L3 auth tests.
/// </summary>
public class ReleaseBuildAuthTests
{
    [Fact]
    public async Task ReleaseBuild_WithLocalUsersConfigured_RefusesToStart()
    {
        var publishDir = Path.Combine(Path.GetTempPath(), $"ci-host-release-{Guid.NewGuid()}");
        await RunDotnetAsync("build", RepoPaths.HostCsproj, "-c", "Release", "-o", publishDir);

        var dllPath = Path.Combine(publishDir, "CiRunner.Host.dll");
        Assert.True(File.Exists(dllPath), $"expected Release build output at {dllPath}");

        var root = Path.Combine(Path.GetTempPath(), $"ci-host-{Guid.NewGuid()}");
        var port = HostProcess.GetFreePort();
        var configPath = HostProcess.WriteConfig(root, port, new[] { new TestLocalUser("admin", "admin123", "admin") }, initialAdmins: new[] { "admin" });

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => HostProcess.StartAsync(root, port, configPath, deleteRootOnDispose: false, hostDllPath: dllPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
            try { Directory.Delete(publishDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static async Task RunDotnetAsync(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveDotnetExe(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start dotnet build");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet {string.Join(' ', args)} failed (exit {process.ExitCode}):\n{stdout}\n{stderr}");
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
}
