using YamlDotNet.Serialization;

namespace MaToolkit.Automation.Shared.Models.Yaml;

public class RunbookDefinition
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "description")]
    public string Description { get; set; } = string.Empty;

    [YamlMember(Alias = "data_source")]
    public DataSourceConfig DataSource { get; set; } = new();

    [YamlMember(Alias = "init")]
    public List<StepDefinition> Init { get; set; } = new();

    [YamlMember(Alias = "phases")]
    public List<PhaseDefinition> Phases { get; set; } = new();

    [YamlMember(Alias = "on_member_removed")]
    public List<StepDefinition> OnMemberRemoved { get; set; } = new();

    [YamlMember(Alias = "rollbacks")]
    public Dictionary<string, List<StepDefinition>> Rollbacks { get; set; } = new();
}
