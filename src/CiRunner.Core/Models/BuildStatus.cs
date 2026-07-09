namespace CiRunner.Core.Models;

public static class BuildStatus
{
    public const string Queued = "queued";
    public const string Waiting = "waiting";
    public const string Running = "running";
    public const string Success = "success";
    public const string Failed = "failed";
    public const string Aborted = "aborted";

    public static bool IsTerminal(string status) =>
        status is Success or Failed or Aborted;
}

public static class BuildTrigger
{
    public const string Hook = "hook";
    public const string Polling = "polling";
    public const string Manual = "manual";
    public const string Rebuild = "rebuild";
    public const string Timer = "timer";
    public const string Api = "api";
}

public static class StepStatus
{
    public const string Running = "running";
    public const string Success = "success";
    public const string Failed = "failed";
    public const string Aborted = "aborted";
}
