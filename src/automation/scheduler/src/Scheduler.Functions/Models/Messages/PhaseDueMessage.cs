using System.Text.Json.Serialization;

namespace Scheduler.Functions.Models.Messages;

public class PhaseDueMessage
{
    [JsonPropertyName("messageType")]
    public string MessageType => "phase-due";

    [JsonPropertyName("runbookName")]
    public string RunbookName { get; set; } = string.Empty;

    [JsonPropertyName("runbookVersion")]
    public int RunbookVersion { get; set; }

    [JsonPropertyName("batchId")]
    public int BatchId { get; set; }

    [JsonPropertyName("phaseExecutionId")]
    public int PhaseExecutionId { get; set; }

    [JsonPropertyName("phaseName")]
    public string PhaseName { get; set; } = string.Empty;

    [JsonPropertyName("offsetMinutes")]
    public int OffsetMinutes { get; set; }

    [JsonPropertyName("dueAt")]
    public DateTime DueAt { get; set; }

    [JsonPropertyName("memberIds")]
    public List<int> MemberIds { get; set; } = new();
}
