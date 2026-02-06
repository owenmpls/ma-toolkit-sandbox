using MaToolkit.Automation.Shared.Constants;

namespace MaToolkit.Automation.Shared.Models.Db;

public class StepExecutionRecord
{
    public int Id { get; set; }
    public int PhaseExecutionId { get; set; }
    public int BatchMemberId { get; set; }
    public string StepName { get; set; } = string.Empty;
    public int StepIndex { get; set; }
    public string? WorkerId { get; set; }
    public string? FunctionName { get; set; }
    public string? ParamsJson { get; set; }
    public string Status { get; set; } = StepStatus.Pending;
    public string? JobId { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool IsPollStep { get; set; }
    public int? PollIntervalSec { get; set; }
    public int? PollTimeoutSec { get; set; }
    public DateTime? PollStartedAt { get; set; }
    public DateTime? LastPolledAt { get; set; }
    public int PollCount { get; set; }
    public string? OnFailure { get; set; }
}
