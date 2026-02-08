using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;
using Microsoft.Extensions.Logging;

namespace Scheduler.Functions.Services;

public interface IPhaseDispatcher
{
    Task EvaluatePendingPhasesAsync(
        RunbookRecord runbook, RunbookDefinition definition, BatchRecord batch, DateTime now);
}

public class PhaseDispatcher : IPhaseDispatcher
{
    private readonly IPhaseExecutionRepository _phaseRepo;
    private readonly IMemberRepository _memberRepo;
    private readonly IServiceBusPublisher _publisher;
    private readonly ILogger<PhaseDispatcher> _logger;

    public PhaseDispatcher(
        IPhaseExecutionRepository phaseRepo,
        IMemberRepository memberRepo,
        IServiceBusPublisher publisher,
        ILogger<PhaseDispatcher> logger)
    {
        _phaseRepo = phaseRepo;
        _memberRepo = memberRepo;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task EvaluatePendingPhasesAsync(
        RunbookRecord runbook, RunbookDefinition definition, BatchRecord batch, DateTime now)
    {
        var pendingPhases = await _phaseRepo.GetPendingDueAsync(batch.Id, now);
        if (!pendingPhases.Any()) return;

        // Load members ONCE for the batch
        var members = (await _memberRepo.GetActiveByBatchAsync(batch.Id)).ToList();

        foreach (var phase in pendingPhases)
        {
            if (members.Count == 0)
            {
                _logger.LogInformation("No active members for phase '{PhaseName}' in batch {BatchId}",
                    phase.PhaseName, batch.Id);
                continue;
            }

            // Dispatch phase-due message (orchestrator creates step executions on receipt)
            await _publisher.PublishPhaseDueAsync(new PhaseDueMessage
            {
                RunbookName = runbook.Name,
                RunbookVersion = phase.RunbookVersion,
                BatchId = batch.Id,
                PhaseExecutionId = phase.Id,
                PhaseName = phase.PhaseName,
                OffsetMinutes = phase.OffsetMinutes,
                DueAt = phase.DueAt,
                MemberIds = members.Select(m => m.Id).ToList()
            });

            await _phaseRepo.SetDispatchedAsync(phase.Id);

            _logger.LogInformation(
                "Dispatched phase '{PhaseName}' for batch {BatchId} with {MemberCount} members",
                phase.PhaseName, batch.Id, members.Count);
        }
    }
}
