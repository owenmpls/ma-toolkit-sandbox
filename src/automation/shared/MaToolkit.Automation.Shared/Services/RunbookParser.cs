using MaToolkit.Automation.Shared.Models.Yaml;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MaToolkit.Automation.Shared.Services;

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

    public List<string> Validate(RunbookDefinition definition)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(definition.Name))
            errors.Add("Runbook name is required");

        if (definition.DataSource is null)
        {
            errors.Add("data_source is required");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(definition.DataSource.Type))
            errors.Add("data_source.type is required");

        if (string.IsNullOrWhiteSpace(definition.DataSource.Connection))
            errors.Add("data_source.connection is required");

        if (string.IsNullOrWhiteSpace(definition.DataSource.Query))
            errors.Add("data_source.query is required");

        if (string.IsNullOrWhiteSpace(definition.DataSource.PrimaryKey))
            errors.Add("data_source.primary_key is required");

        if (string.IsNullOrWhiteSpace(definition.DataSource.BatchTimeColumn) &&
            !string.Equals(definition.DataSource.BatchTime, "immediate", StringComparison.OrdinalIgnoreCase))
            errors.Add("data_source must specify either batch_time_column or batch_time: immediate");

        if (definition.DataSource.Type?.ToLowerInvariant() == "databricks" &&
            string.IsNullOrWhiteSpace(definition.DataSource.WarehouseId))
            errors.Add("data_source.warehouse_id is required for databricks type");

        if (definition.Phases.Count == 0)
            errors.Add("At least one phase is required");

        foreach (var phase in definition.Phases)
        {
            if (string.IsNullOrWhiteSpace(phase.Name))
                errors.Add("Phase name is required");

            if (string.IsNullOrWhiteSpace(phase.Offset))
                errors.Add($"Phase '{phase.Name}' offset is required");
            else if (!IsValidOffset(phase.Offset))
                errors.Add($"Phase '{phase.Name}' has invalid offset format: {phase.Offset}");

            if (phase.Steps.Count == 0)
                errors.Add($"Phase '{phase.Name}' must have at least one step");

            foreach (var step in phase.Steps)
            {
                ValidateStep(step, $"Phase '{phase.Name}'", definition.Rollbacks, errors);
            }
        }

        foreach (var step in definition.Init)
        {
            ValidateStep(step, "Init", definition.Rollbacks, errors);
        }

        foreach (var step in definition.OnMemberRemoved)
        {
            ValidateStep(step, "on_member_removed", definition.Rollbacks, errors);
        }

        if (errors.Count > 0)
            _logger.LogWarning("Runbook validation failed with {ErrorCount} errors", errors.Count);

        return errors;
    }

    private static void ValidateStep(StepDefinition step, string context,
        Dictionary<string, List<StepDefinition>> rollbacks, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(step.Name))
            errors.Add($"{context}: step name is required");

        if (string.IsNullOrWhiteSpace(step.WorkerId))
            errors.Add($"{context}, step '{step.Name}': worker_id is required");

        if (string.IsNullOrWhiteSpace(step.Function))
            errors.Add($"{context}, step '{step.Name}': function is required");

        if (!string.IsNullOrWhiteSpace(step.OnFailure) && !rollbacks.ContainsKey(step.OnFailure))
            errors.Add($"{context}, step '{step.Name}': on_failure references unknown rollback '{step.OnFailure}'");

        if (step.Poll is not null)
        {
            if (string.IsNullOrWhiteSpace(step.Poll.Interval))
                errors.Add($"{context}, step '{step.Name}': poll.interval is required");
            if (string.IsNullOrWhiteSpace(step.Poll.Timeout))
                errors.Add($"{context}, step '{step.Name}': poll.timeout is required");
        }
    }

    private static bool IsValidOffset(string offset)
    {
        if (offset == "T-0") return true;

        if (!offset.StartsWith("T-")) return false;

        var value = offset[2..];
        if (value.Length < 2) return false;

        var suffix = value[^1];
        var number = value[..^1];

        return suffix is 'd' or 'h' or 'm' or 's' && int.TryParse(number, out var n) && n > 0;
    }
}
