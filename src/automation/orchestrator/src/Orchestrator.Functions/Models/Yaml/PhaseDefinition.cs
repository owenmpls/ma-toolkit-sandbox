using YamlDotNet.Serialization;

namespace Orchestrator.Functions.Models.Yaml;

public class PhaseDefinition
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "offset")]
    public string Offset { get; set; } = string.Empty;

    [YamlMember(Alias = "steps")]
    public List<StepDefinition> Steps { get; set; } = new();
}
