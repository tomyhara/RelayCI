namespace CiRunner.Host.Tests.Support;

/// <summary>Locates repo-relative paths (the Host project's own .csproj) from the test output directory.</summary>
public static class RepoPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();
    public static string HostCsproj => Path.Combine(RepoRoot, "src", "CiRunner.Host", "CiRunner.Host.csproj");

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
