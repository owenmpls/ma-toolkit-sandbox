using System.Text.Json.Serialization;
using MaToolkit.Automation.Shared.Constants;

namespace MaToolkit.Automation.Shared.Models.Messages;

public class BatchInitMessage
{
    [JsonPropertyName("messageType")]
    public string MessageType => MessageTypes.BatchInit;

    [JsonPropertyName("runbookName")]
    public string RunbookName { get; set; } = string.Empty;

    [JsonPropertyName("runbookVersion")]
    public int RunbookVersion { get; set; }

    [JsonPropertyName("batchId")]
    public int BatchId { get; set; }

    [JsonPropertyName("batchStartTime")]
    public DateTime? BatchStartTime { get; set; }

    [JsonPropertyName("memberCount")]
    public int MemberCount { get; set; }
}
