using System.Text.RegularExpressions;
using MaToolkit.Automation.Shared.Models.Yaml;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MaToolkit.Automation.Shared.Services;

public class RunbookParser : IRunbookParser
{
    private readonly ILogger<RunbookParser> _logger;
    private readonly IPhaseEvaluator? _phaseEvaluator;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly HashSet<string> ValidDataSourceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "dataverse", "databricks"
    };

    private static readonly HashSet<string> ValidMultiValuedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "semicolon_delimited", "comma_delimited", "json_array"
    };

    private static readonly Regex TemplatePattern = new(@"\{\{[^}]*\}\}|\{\{[^}]*$|\{[^{]", RegexOptions.Compiled);

    public RunbookParser(ILogger<RunbookParser> logger, IPhaseEvaluator? phaseEvaluator = null)
    {
        _logger = logger;
        _phaseEvaluator = phaseEvaluator;
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
        else if (!ValidDataSourceTypes.Contains(definition.DataSource.Type))
            errors.Add($"data_source.type '{definition.DataSource.Type}' is not supported. Valid types: {string.Join(", ", ValidDataSourceTypes)}");

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

        // Validate multi-valued column formats
        foreach (var col in definition.DataSource.MultiValuedColumns)
        {
            if (!string.IsNullOrWhiteSpace(col.Format) && !ValidMultiValuedFormats.Contains(col.Format))
                errors.Add($"Multi-valued column '{col.Name}' has invalid format '{col.Format}'. Valid formats: {string.Join(", ", ValidMultiValuedFormats)}");
        }

        if (definition.Phases.Count == 0)
            errors.Add("At least one phase is required");

        // Check for duplicate phase names
        var phaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var phase in definition.Phases)
        {
            if (!string.IsNullOrWhiteSpace(phase.Name) && !phaseNames.Add(phase.Name))
                errors.Add($"Duplicate phase name: '{phase.Name}'");
        }

        foreach (var phase in definition.Phases)
        {
            if (string.IsNullOrWhiteSpace(phase.Name))
                errors.Add("Phase name is required");

            if (string.IsNullOrWhiteSpace(phase.Offset))
                errors.Add($"Phase '{phase.Name}' offset is required");
            else
            {
                if (!IsValidOffset(phase.Offset))
                    errors.Add($"Phase '{phase.Name}' has invalid offset format: {phase.Offset}");

                // Use PhaseEvaluator to validate the offset can be parsed
                if (_phaseEvaluator is not null)
                {
                    try
                    {
                        _phaseEvaluator.ParseOffsetMinutes(phase.Offset);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Phase '{phase.Name}' offset '{phase.Offset}' cannot be parsed: {ex.Message}");
                    }
                }
            }

            if (phase.Steps.Count == 0)
                errors.Add($"Phase '{phase.Name}' must have at least one step");

            // Check for duplicate step names within the phase
            var stepNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var step in phase.Steps)
            {
                if (!string.IsNullOrWhiteSpace(step.Name) && !stepNames.Add(step.Name))
                    errors.Add($"Phase '{phase.Name}' has duplicate step name: '{step.Name}'");
            }

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

    private void ValidateStep(StepDefinition step, string context,
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
            else if (_phaseEvaluator is not null)
            {
                try
                {
                    _phaseEvaluator.ParseDurationSeconds(step.Poll.Interval);
                }
                catch (Exception ex)
                {
                    errors.Add($"{context}, step '{step.Name}': poll.interval '{step.Poll.Interval}' cannot be parsed: {ex.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(step.Poll.Timeout))
                errors.Add($"{context}, step '{step.Name}': poll.timeout is required");
            else if (_phaseEvaluator is not null)
            {
                try
                {
                    _phaseEvaluator.ParseDurationSeconds(step.Poll.Timeout);
                }
                catch (Exception ex)
                {
                    errors.Add($"{context}, step '{step.Name}': poll.timeout '{step.Poll.Timeout}' cannot be parsed: {ex.Message}");
                }
            }
        }

        // Validate template syntax in params
        if (step.Params is not null)
        {
            foreach (var (key, value) in step.Params)
            {
                if (value is string strValue)
                {
                    ValidateTemplateSyntax(strValue, $"{context}, step '{step.Name}', param '{key}'", errors);
                }
            }
        }
    }

    private static void ValidateTemplateSyntax(string value, string context, List<string> errors)
    {
        var openBraces = 0;
        for (var i = 0; i < value.Length - 1; i++)
        {
            if (value[i] == '{' && value[i + 1] == '{')
            {
                openBraces++;
                i++; // Skip next brace
            }
            else if (value[i] == '}' && value[i + 1] == '}')
            {
                openBraces--;
                if (openBraces < 0)
                {
                    errors.Add($"{context}: unmatched closing braces '}}}}' in template");
                    return;
                }
                i++; // Skip next brace
            }
        }

        if (openBraces > 0)
        {
            errors.Add($"{context}: unclosed template braces '{{{{' in value");
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
