namespace CiRunner.Core.Models;

public sealed class HookRecord
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public string? Secret { get; set; }
    public required string HandlerPath { get; set; }
    public int TimeoutSec { get; set; } = 60;
    public bool Enabled { get; set; } = true;
    public required string CreatedAt { get; set; }
}

public sealed class HookRunRecord
{
    public long Id { get; set; }
    public long HookId { get; set; }
    public string? DeliveryId { get; set; }
    public string? Event { get; set; }
    public required string ReceivedAt { get; set; }
    public required string Status { get; set; }
    public string TriggeredBuilds { get; set; } = "[]";
    public string? PayloadPath { get; set; }
    public string? LogPath { get; set; }
}

public static class HookRunStatus
{
    public const string Running = "running";
    public const string Success = "success";
    public const string Failed = "failed";
    public const string Timeout = "timeout";
}
