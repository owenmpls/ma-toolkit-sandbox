using System.Text.Json.Serialization;
using MaToolkit.Automation.Shared.Constants;

namespace MaToolkit.Automation.Shared.Models.Messages;

public class RetryCheckMessage
{
    [JsonPropertyName("messageType")]
    public string MessageType => MessageTypes.RetryCheck;

    [JsonPropertyName("stepExecutionId")]
    public int StepExecutionId { get; set; }

    [JsonPropertyName("isInitStep")]
    public bool IsInitStep { get; set; }

    [JsonPropertyName("runbookName")]
    public string RunbookName { get; set; } = string.Empty;

    [JsonPropertyName("runbookVersion")]
    public int RunbookVersion { get; set; }

    [JsonPropertyName("batchId")]
    public int BatchId { get; set; }
}
