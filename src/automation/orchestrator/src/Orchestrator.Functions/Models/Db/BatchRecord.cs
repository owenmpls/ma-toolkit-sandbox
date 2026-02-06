namespace Orchestrator.Functions.Models.Db;

public class BatchRecord
{
    public int Id { get; set; }
    public int RunbookId { get; set; }
    public DateTime BatchStartTime { get; set; }
    public string Status { get; set; } = "detected";
    public DateTime DetectedAt { get; set; }
    public DateTime? InitDispatchedAt { get; set; }
}
