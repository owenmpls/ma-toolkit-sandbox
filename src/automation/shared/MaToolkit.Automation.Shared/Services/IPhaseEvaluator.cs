using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Yaml;

namespace MaToolkit.Automation.Shared.Services;

public interface IPhaseEvaluator
{
    int ParseOffsetMinutes(string offset);
    int ParseDurationSeconds(string duration);
    DateTime CalculateDueAt(DateTime batchStartTime, int offsetMinutes);
    List<PhaseExecutionRecord> CreatePhaseExecutions(
        int batchId, DateTime batchStartTime, RunbookDefinition definition, int runbookVersion);
    List<PhaseExecutionRecord> HandleVersionTransition(
        IEnumerable<PhaseExecutionRecord> existingPhases,
        int batchId, DateTime batchStartTime,
        RunbookDefinition newDefinition, int newVersion,
        string overdueBehavior, bool ignoreOverdueApplied);
}
