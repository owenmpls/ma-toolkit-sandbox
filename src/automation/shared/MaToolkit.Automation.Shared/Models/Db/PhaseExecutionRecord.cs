using MaToolkit.Automation.Shared.Constants;

namespace MaToolkit.Automation.Shared.Models.Db;

public class PhaseExecutionRecord
{
    public int Id { get; set; }
    public int BatchId { get; set; }
    public string PhaseName { get; set; } = string.Empty;
    public int OffsetMinutes { get; set; }
    public DateTime? DueAt { get; set; } // Nullable for manual batches
    public int RunbookVersion { get; set; }
    public string Status { get; set; } = PhaseStatus.Pending;
    public DateTime? DispatchedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
