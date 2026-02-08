using YamlDotNet.Serialization;

namespace MaToolkit.Automation.Shared.Models.Yaml;

public class RetryConfig
{
    [YamlMember(Alias = "max_retries")]
    public int MaxRetries { get; set; }

    [YamlMember(Alias = "interval")]
    public string Interval { get; set; } = string.Empty;
}
