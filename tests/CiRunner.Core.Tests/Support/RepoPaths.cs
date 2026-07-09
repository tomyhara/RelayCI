namespace CiRunner.Core.Tests.Support;

/// <summary>Locates repo-relative fixtures (the real psmodule/bootstrap.ps1) from the test output directory.</summary>
public static class RepoPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();
    public static string PsModuleDir => Path.Combine(RepoRoot, "src", "CiRunner.Host", "psmodule");
    public static string BootstrapScript => Path.Combine(PsModuleDir, "bootstrap.ps1");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CiRunner.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (CiRunner.sln) above " + AppContext.BaseDirectory);
    }
}
