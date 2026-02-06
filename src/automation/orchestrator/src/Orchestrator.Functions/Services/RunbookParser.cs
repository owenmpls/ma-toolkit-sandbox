using Microsoft.Extensions.Logging;
using Orchestrator.Functions.Models.Yaml;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Orchestrator.Functions.Services;

public interface IRunbookParser
{
    RunbookDefinition Parse(string yamlContent);
}

public class RunbookParser : IRunbookParser
{
    private readonly ILogger<RunbookParser> _logger;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public RunbookParser(ILogger<RunbookParser> logger)
    {
        _logger = logger;
    }

    public RunbookDefinition Parse(string yamlContent)
    {
        return Deserializer.Deserialize<RunbookDefinition>(yamlContent);
    }
}
