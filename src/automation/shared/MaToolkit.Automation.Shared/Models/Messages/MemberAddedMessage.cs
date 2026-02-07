using System.Text.Json.Serialization;
using MaToolkit.Automation.Shared.Constants;

namespace MaToolkit.Automation.Shared.Models.Messages;

public class MemberAddedMessage
{
    [JsonPropertyName("messageType")]
    public string MessageType => MessageTypes.MemberAdded;

    [JsonPropertyName("runbookName")]
    public string RunbookName { get; set; } = string.Empty;

    [JsonPropertyName("runbookVersion")]
    public int RunbookVersion { get; set; }

    [JsonPropertyName("batchId")]
    public int BatchId { get; set; }

    [JsonPropertyName("batchMemberId")]
    public int BatchMemberId { get; set; }

    [JsonPropertyName("memberKey")]
    public string MemberKey { get; set; } = string.Empty;
}
