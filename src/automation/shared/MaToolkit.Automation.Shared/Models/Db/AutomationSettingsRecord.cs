namespace MaToolkit.Automation.Shared.Models.Db;

public class AutomationSettingsRecord
{
    public string RunbookName { get; set; } = string.Empty;
    public bool AutomationEnabled { get; set; }
    public DateTime? EnabledAt { get; set; }
    public string? EnabledBy { get; set; }
    public DateTime? DisabledAt { get; set; }
    public string? DisabledBy { get; set; }
}
