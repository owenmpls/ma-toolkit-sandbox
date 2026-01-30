using System.Text.Json.Serialization;

namespace Scheduler.Functions.Models.Messages;

public class BatchInitMessage
{
    [JsonPropertyName("messageType")]
    public string MessageType => "batch-init";

    [JsonPropertyName("runbookName")]
    public string RunbookName { get; set; } = string.Empty;

    [JsonPropertyName("runbookVersion")]
    public int RunbookVersion { get; set; }

    [JsonPropertyName("batchId")]
    public int BatchId { get; set; }

    [JsonPropertyName("batchStartTime")]
    public DateTime BatchStartTime { get; set; }

    [JsonPropertyName("memberCount")]
    public int MemberCount { get; set; }
}
