using System.Text.Json.Serialization;
using MaToolkit.Automation.Shared.Constants;

namespace MaToolkit.Automation.Shared.Models.Messages;

public class PhaseDueMessage
{
    [JsonPropertyName("messageType")]
    public string MessageType => MessageTypes.PhaseDue;

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
    public DateTime? DueAt { get; set; }

    [JsonPropertyName("memberIds")]
    public List<int> MemberIds { get; set; } = new();
}
