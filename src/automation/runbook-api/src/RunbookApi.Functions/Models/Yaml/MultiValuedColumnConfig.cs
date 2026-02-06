using YamlDotNet.Serialization;

namespace RunbookApi.Functions.Models.Yaml;

public class MultiValuedColumnConfig
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "format")]
    public string Format { get; set; } = string.Empty;
}
