using Microsoft.Extensions.Logging;
using Orchestrator.Functions.Models.Db;
using Orchestrator.Functions.Models.Yaml;

namespace Orchestrator.Functions.Services;

public interface IPhaseEvaluator
{
    int ParseOffsetMinutes(string offset);
    int ParseDurationSeconds(string duration);
    DateTime CalculateDueAt(DateTime batchStartTime, int offsetMinutes);
    PhaseDefinition? GetPhaseByName(RunbookDefinition definition, string phaseName);
}

public class PhaseEvaluator : IPhaseEvaluator
{
    private readonly ILogger<PhaseEvaluator> _logger;

    public PhaseEvaluator(ILogger<PhaseEvaluator> logger)
    {
        _logger = logger;
    }

    public int ParseOffsetMinutes(string offset)
    {
        if (offset == "T-0") return 0;

        if (!offset.StartsWith("T-"))
            throw new ArgumentException($"Invalid offset format: {offset}");

        var value = offset[2..];
        var suffix = value[^1];
        var number = int.Parse(value[..^1]);

        return suffix switch
        {
            'd' => number * 24 * 60,
            'h' => number * 60,
            'm' => number,
            's' => (int)Math.Ceiling(number / 60.0),
            _ => throw new ArgumentException($"Invalid offset suffix: {suffix}")
        };
    }

    public int ParseDurationSeconds(string duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
            return 0;

        var suffix = duration[^1];
        var number = int.Parse(duration[..^1]);

        return suffix switch
        {
            'd' => number * 24 * 60 * 60,
            'h' => number * 60 * 60,
            'm' => number * 60,
            's' => number,
            _ => throw new ArgumentException($"Invalid duration suffix: {suffix}")
        };
    }

    public DateTime CalculateDueAt(DateTime batchStartTime, int offsetMinutes)
    {
        // offset is "T minus", so due_at = batchStartTime - offset
        return batchStartTime.AddMinutes(-offsetMinutes);
    }

    public PhaseDefinition? GetPhaseByName(RunbookDefinition definition, string phaseName)
    {
        return definition.Phases.FirstOrDefault(p => p.Name == phaseName);
    }
}
