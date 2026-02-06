namespace MaToolkit.Automation.Shared.Constants;

/// <summary>
/// Status values for step execution lifecycle.
/// </summary>
public static class StepStatus
{
    public const string Pending = "pending";
    public const string Dispatched = "dispatched";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Polling = "polling";
    public const string PollTimeout = "poll_timeout";
    public const string Cancelled = "cancelled";
}
