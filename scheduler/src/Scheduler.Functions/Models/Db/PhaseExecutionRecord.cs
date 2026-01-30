namespace Scheduler.Functions.Models.Db;

public class PhaseExecutionRecord
{
    public int Id { get; set; }
    public int BatchId { get; set; }
    public string PhaseName { get; set; } = string.Empty;
    public int OffsetMinutes { get; set; }
    public DateTime DueAt { get; set; }
    public int RunbookVersion { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime? DispatchedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
