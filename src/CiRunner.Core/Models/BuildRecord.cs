namespace CiRunner.Core.Models;

public sealed class BuildRecord
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public int Number { get; set; }
    public required string Status { get; set; }
    public required string Trigger { get; set; }
    public string Parameters { get; set; } = "{}";
    public string? DedupKey { get; set; }
    public string? CommitSha { get; set; }
    public string? Branch { get; set; }
    public int? PrNumber { get; set; }
    public required string QueuedAt { get; set; }
    public string? StartedAt { get; set; }
    public string? FinishedAt { get; set; }
    public string? Note { get; set; }
}

public sealed class BuildStepRecord
{
    public long Id { get; set; }
    public long BuildId { get; set; }
    public int Seq { get; set; }
    public required string Name { get; set; }
    public required string Status { get; set; }
    public string? Post { get; set; }
    public string? StartedAt { get; set; }
    public string? FinishedAt { get; set; }
    public long? LogOffsetStart { get; set; }
    public long? LogOffsetEnd { get; set; }
    public string? Error { get; set; }
}
