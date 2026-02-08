using MaToolkit.Automation.Shared.Constants;

namespace MaToolkit.Automation.Shared.Models.Db;

public class RunbookRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; }
    public string YamlContent { get; set; } = string.Empty;
    public string DataTableName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string OverdueBehavior { get; set; } = Constants.OverdueBehavior.Rerun;
    public bool IgnoreOverdueApplied { get; set; }
    public bool RerunInit { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorAt { get; set; }
}
