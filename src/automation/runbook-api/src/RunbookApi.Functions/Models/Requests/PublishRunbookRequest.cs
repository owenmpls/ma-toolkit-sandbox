namespace RunbookApi.Functions.Models.Requests;

public class PublishRunbookRequest
{
    public string Name { get; set; } = string.Empty;
    public string YamlContent { get; set; } = string.Empty;
    public string? OverdueBehavior { get; set; }
    public bool RerunInit { get; set; }
}
