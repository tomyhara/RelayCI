namespace CiRunner.Core.Models;

public sealed class TestResultRecord
{
    public long Id { get; set; }
    public long BuildId { get; set; }
    public string? Suite { get; set; }
    public required string Name { get; set; }
    public required string Status { get; set; }
    public long? DurationMs { get; set; }
    public string? Message { get; set; }
}

public static class TestCaseStatus
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Error = "error";
}

public sealed class ArtifactRecord
{
    public long Id { get; set; }
    public long BuildId { get; set; }
    public required string Path { get; set; }
    public long? Size { get; set; }
}
