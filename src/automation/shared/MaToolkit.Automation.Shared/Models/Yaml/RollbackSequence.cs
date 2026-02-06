namespace MaToolkit.Automation.Shared.Models.Yaml;

public class RollbackSequence
{
    public string Name { get; set; } = string.Empty;
    public List<StepDefinition> Steps { get; set; } = new();
}
