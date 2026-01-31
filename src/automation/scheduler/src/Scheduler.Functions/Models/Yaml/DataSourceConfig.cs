using YamlDotNet.Serialization;

namespace Scheduler.Functions.Models.Yaml;

public class DataSourceConfig
{
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = string.Empty;

    [YamlMember(Alias = "connection")]
    public string Connection { get; set; } = string.Empty;

    [YamlMember(Alias = "warehouse_id")]
    public string? WarehouseId { get; set; }

    [YamlMember(Alias = "query")]
    public string Query { get; set; } = string.Empty;

    [YamlMember(Alias = "primary_key")]
    public string PrimaryKey { get; set; } = string.Empty;

    [YamlMember(Alias = "batch_time_column")]
    public string? BatchTimeColumn { get; set; }

    [YamlMember(Alias = "batch_time")]
    public string? BatchTime { get; set; }

    [YamlMember(Alias = "multi_valued_columns")]
    public List<MultiValuedColumnConfig> MultiValuedColumns { get; set; } = new();
}
