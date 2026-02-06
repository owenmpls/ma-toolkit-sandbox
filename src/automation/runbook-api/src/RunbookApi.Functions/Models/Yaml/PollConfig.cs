using YamlDotNet.Serialization;

namespace RunbookApi.Functions.Models.Yaml;

public class PollConfig
{
    [YamlMember(Alias = "interval")]
    public string Interval { get; set; } = string.Empty;

    [YamlMember(Alias = "timeout")]
    public string Timeout { get; set; } = string.Empty;
}
