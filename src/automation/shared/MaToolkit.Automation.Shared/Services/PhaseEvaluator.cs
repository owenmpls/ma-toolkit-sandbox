using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Yaml;
using Microsoft.Extensions.Logging;

namespace MaToolkit.Automation.Shared.Services;

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
        if (value.Length < 2)
            throw new ArgumentException($"Invalid offset format: {offset}");

        var suffix = value[^1];
        var numberPart = value[..^1];

        if (!int.TryParse(numberPart, out var number))
            throw new FormatException($"Invalid offset number: {offset}");

        return suffix switch
        {
            'd' => number * 24 * 60,
            'h' => number * 60,
            'm' => number,
            // Seconds are rounded UP to the nearest minute because the scheduler
            // evaluates phases on a 5-minute timer — sub-minute precision isn't
            // achievable, and rounding up ensures the phase is never dispatched
            // before its intended offset.
            's' => (int)Math.Ceiling(number / 60.0),
            _ => throw new ArgumentException($"Invalid offset suffix: {suffix}")
        };
    }

    public int ParseDurationSeconds(string duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
            return 0;

        if (duration.Length < 2)
            throw new ArgumentException($"Invalid duration format: {duration}");

        var suffix = duration[^1];
        var numberPart = duration[..^1];

        if (!int.TryParse(numberPart, out var number))
            throw new FormatException($"Invalid duration number: {duration}");

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

    public DateTime? CalculateDueAtNullable(DateTime? batchStartTime, int offsetMinutes)
    {
        if (batchStartTime is null)
            return null;
        return batchStartTime.Value.AddMinutes(-offsetMinutes);
    }

    public List<PhaseExecutionRecord> CreatePhaseExecutions(
        int batchId, DateTime? batchStartTime, RunbookDefinition definition, int runbookVersion)
    {
        var records = new List<PhaseExecutionRecord>();

        foreach (var phase in definition.Phases)
        {
            var offsetMinutes = ParseOffsetMinutes(phase.Offset);
            var dueAt = CalculateDueAtNullable(batchStartTime, offsetMinutes);

            records.Add(new PhaseExecutionRecord
            {
                BatchId = batchId,
                PhaseName = phase.Name,
                OffsetMinutes = offsetMinutes,
                DueAt = dueAt,
                RunbookVersion = runbookVersion,
                Status = PhaseStatus.Pending
            });
        }

        return records;
    }

    public List<PhaseExecutionRecord> HandleVersionTransition(
        IEnumerable<PhaseExecutionRecord> existingPhases,
        int batchId, DateTime? batchStartTime,
        RunbookDefinition newDefinition, int newVersion,
        string overdueBehavior, bool ignoreOverdueApplied)
    {
        var existingSet = existingPhases
            .Where(p => p.RunbookVersion < newVersion)
            .Select(p => p.PhaseName)
            .ToHashSet();

        if (existingSet.Count == 0)
            return new List<PhaseExecutionRecord>();

        var newPhases = new List<PhaseExecutionRecord>();
        var now = DateTime.UtcNow;

        foreach (var phase in newDefinition.Phases)
        {
            var offsetMinutes = ParseOffsetMinutes(phase.Offset);
            var dueAt = CalculateDueAtNullable(batchStartTime, offsetMinutes);
            var isOverdue = dueAt.HasValue && dueAt.Value <= now;

            var status = PhaseStatus.Pending;

            if (isOverdue && overdueBehavior == OverdueBehavior.Ignore && !ignoreOverdueApplied)
            {
                status = PhaseStatus.Skipped;
                _logger.LogInformation(
                    "Skipping overdue phase '{PhaseName}' for batch {BatchId} (ignore mode, first run)",
                    phase.Name, batchId);
            }
            else if (isOverdue && overdueBehavior == OverdueBehavior.Rerun)
            {
                _logger.LogInformation(
                    "Re-creating overdue phase '{PhaseName}' for batch {BatchId} (rerun mode)",
                    phase.Name, batchId);
            }

            newPhases.Add(new PhaseExecutionRecord
            {
                BatchId = batchId,
                PhaseName = phase.Name,
                OffsetMinutes = offsetMinutes,
                DueAt = dueAt,
                RunbookVersion = newVersion,
                Status = status
            });
        }

        return newPhases;
    }
}
