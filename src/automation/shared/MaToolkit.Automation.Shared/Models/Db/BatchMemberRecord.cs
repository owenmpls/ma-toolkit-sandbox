using MaToolkit.Automation.Shared.Constants;

namespace MaToolkit.Automation.Shared.Models.Db;

public class BatchMemberRecord
{
    public int Id { get; set; }
    public int BatchId { get; set; }
    public string MemberKey { get; set; } = string.Empty;
    public string? DataJson { get; set; }
    public string? WorkerDataJson { get; set; }
    public string Status { get; set; } = MemberStatus.Active;
    public DateTime AddedAt { get; set; }
    public DateTime? RemovedAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime? AddDispatchedAt { get; set; }
    public DateTime? RemoveDispatchedAt { get; set; }
}
