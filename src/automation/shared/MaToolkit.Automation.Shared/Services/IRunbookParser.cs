using MaToolkit.Automation.Shared.Models.Yaml;

namespace MaToolkit.Automation.Shared.Services;

public interface IRunbookParser
{
    RunbookDefinition Parse(string yamlContent);
    List<string> Validate(RunbookDefinition definition);
}
