using YamlDotNet.Serialization;

namespace Orchestrator.Functions.Models.Yaml;

public class StepDefinition
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "worker_id")]
    public string WorkerId { get; set; } = string.Empty;

    [YamlMember(Alias = "function")]
    public string Function { get; set; } = string.Empty;

    [YamlMember(Alias = "params")]
    public Dictionary<string, string> Params { get; set; } = new();

    [YamlMember(Alias = "on_failure")]
    public string? OnFailure { get; set; }

    [YamlMember(Alias = "poll")]
    public PollConfig? Poll { get; set; }
}
