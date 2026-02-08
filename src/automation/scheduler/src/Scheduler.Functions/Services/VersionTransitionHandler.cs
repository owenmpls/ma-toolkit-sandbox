using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;
using Microsoft.Extensions.Logging;

namespace Scheduler.Functions.Services;

public interface IVersionTransitionHandler
{
    Task HandleVersionTransitionAsync(
        RunbookRecord runbook, RunbookDefinition definition, BatchRecord batch, DateTime now);
}

public class VersionTransitionHandler : IVersionTransitionHandler
{
    private readonly IRunbookRepository _runbookRepo;
    private readonly IPhaseExecutionRepository _phaseRepo;
    private readonly IMemberRepository _memberRepo;
    private readonly IPhaseEvaluator _phaseEvaluator;
    private readonly IServiceBusPublisher _publisher;
    private readonly ILogger<VersionTransitionHandler> _logger;

    public VersionTransitionHandler(
        IRunbookRepository runbookRepo,
        IPhaseExecutionRepository phaseRepo,
        IMemberRepository memberRepo,
        IPhaseEvaluator phaseEvaluator,
        IServiceBusPublisher publisher,
        ILogger<VersionTransitionHandler> logger)
    {
        _runbookRepo = runbookRepo;
        _phaseRepo = phaseRepo;
        _memberRepo = memberRepo;
        _phaseEvaluator = phaseEvaluator;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task HandleVersionTransitionAsync(
        RunbookRecord runbook, RunbookDefinition definition, BatchRecord batch, DateTime now)
    {
        var existingPhases = (await _phaseRepo.GetByBatchAsync(batch.Id)).ToList();

        // Check if there are phases from older versions only
        var hasOldVersionPhases = existingPhases.Any(p => p.RunbookVersion < runbook.Version);
        var hasCurrentVersionPhases = existingPhases.Any(p => p.RunbookVersion == runbook.Version);

        if (!hasOldVersionPhases || hasCurrentVersionPhases)
            return;

        _logger.LogInformation(
            "Version transition detected for batch {BatchId}: creating v{Version} phases",
            batch.Id, runbook.Version);

        var newPhases = _phaseEvaluator.HandleVersionTransition(
            existingPhases, batch.Id, batch.BatchStartTime,
            definition, runbook.Version,
            runbook.OverdueBehavior, runbook.IgnoreOverdueApplied);

        foreach (var phase in newPhases)
        {
            await _phaseRepo.InsertAsync(phase);
        }

        // Supersede old-version pending phases to prevent double execution
        var superseded = await _phaseRepo.SupersedeOldVersionPendingAsync(batch.Id, runbook.Version);
        if (superseded > 0)
            _logger.LogInformation("Superseded {Count} old-version pending phases for batch {BatchId}",
                superseded, batch.Id);

        if (runbook.OverdueBehavior == OverdueBehavior.Ignore && !runbook.IgnoreOverdueApplied)
        {
            await _runbookRepo.SetIgnoreOverdueAppliedAsync(runbook.Id);
        }

        // Handle init rerun if configured — orchestrator creates the init_executions on demand
        if (runbook.RerunInit && definition.Init.Count > 0)
        {
            await _publisher.PublishBatchInitAsync(new BatchInitMessage
            {
                RunbookName = runbook.Name,
                RunbookVersion = runbook.Version,
                BatchId = batch.Id,
                BatchStartTime = batch.BatchStartTime,
                MemberCount = (await _memberRepo.GetActiveByBatchAsync(batch.Id)).Count()
            });

            _logger.LogInformation(
                "Re-running init steps for batch {BatchId} on version transition to v{Version}",
                batch.Id, runbook.Version);
        }
    }
}
