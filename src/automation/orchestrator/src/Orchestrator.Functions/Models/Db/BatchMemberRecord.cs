namespace Orchestrator.Functions.Models.Db;

public class BatchMemberRecord
{
    public int Id { get; set; }
    public int BatchId { get; set; }
    public string MemberKey { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public DateTime AddedAt { get; set; }
    public DateTime? RemovedAt { get; set; }
    public DateTime? AddDispatchedAt { get; set; }
    public DateTime? RemoveDispatchedAt { get; set; }
}
