using System.Text.Json.Serialization;

namespace Scheduler.Functions.Models.Messages;

public class PollCheckMessage
{
    [JsonPropertyName("messageType")]
    public string MessageType => "poll-check";

    [JsonPropertyName("runbookName")]
    public string RunbookName { get; set; } = string.Empty;

    [JsonPropertyName("runbookVersion")]
    public int RunbookVersion { get; set; }

    [JsonPropertyName("batchId")]
    public int BatchId { get; set; }

    [JsonPropertyName("stepExecutionId")]
    public int StepExecutionId { get; set; }

    [JsonPropertyName("stepName")]
    public string StepName { get; set; } = string.Empty;

    [JsonPropertyName("pollCount")]
    public int PollCount { get; set; }
}
