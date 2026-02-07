namespace MaToolkit.Automation.Shared.Constants;

/// <summary>
/// Service Bus message type identifiers for orchestrator events.
/// </summary>
public static class MessageTypes
{
    public const string BatchInit = "batch-init";
    public const string PhaseDue = "phase-due";
    public const string MemberAdded = "member-added";
    public const string MemberRemoved = "member-removed";
    public const string PollCheck = "poll-check";
}
