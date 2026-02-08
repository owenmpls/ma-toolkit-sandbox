using System.Text.Json;
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
    private readonly IInitExecutionRepository _initRepo;
    private readonly IMemberRepository _memberRepo;
    private readonly IPhaseEvaluator _phaseEvaluator;
    private readonly IServiceBusPublisher _publisher;
    private readonly ILogger<VersionTransitionHandler> _logger;

    public VersionTransitionHandler(
        IRunbookRepository runbookRepo,
        IPhaseExecutionRepository phaseRepo,
        IInitExecutionRepository initRepo,
        IMemberRepository memberRepo,
        IPhaseEvaluator phaseEvaluator,
        IServiceBusPublisher publisher,
        ILogger<VersionTransitionHandler> logger)
    {
        _runbookRepo = runbookRepo;
        _phaseRepo = phaseRepo;
        _initRepo = initRepo;
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

        // Handle init rerun if configured
        if (runbook.RerunInit && definition.Init.Count > 0)
        {
            var existingInits = await _initRepo.GetByBatchAsync(batch.Id);
            var hasCurrentInits = existingInits.Any(i => i.RunbookVersion == runbook.Version);

            if (!hasCurrentInits)
            {
                for (int i = 0; i < definition.Init.Count; i++)
                {
                    var initStep = definition.Init[i];
                    await _initRepo.InsertAsync(new InitExecutionRecord
                    {
                        BatchId = batch.Id,
                        StepName = initStep.Name,
                        StepIndex = i,
                        RunbookVersion = runbook.Version,
                        WorkerId = initStep.WorkerId,
                        FunctionName = initStep.Function,
                        ParamsJson = initStep.Params.Count > 0
                            ? JsonSerializer.Serialize(ResolveInitParams(initStep.Params, batch.Id, batch.BatchStartTime))
                            : null,
                        IsPollStep = initStep.Poll is not null,
                        PollIntervalSec = initStep.Poll is not null
                            ? _phaseEvaluator.ParseDurationSeconds(initStep.Poll.Interval) : null,
                        PollTimeoutSec = initStep.Poll is not null
                            ? _phaseEvaluator.ParseDurationSeconds(initStep.Poll.Timeout) : null,
                        OnFailure = initStep.OnFailure
                    });
                }

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

    private Dictionary<string, string> ResolveInitParams(
        Dictionary<string, string> paramTemplates, int batchId, DateTime? batchStartTime)
    {
        var resolved = new Dictionary<string, string>();
        foreach (var (key, template) in paramTemplates)
        {
            var value = template
                .Replace("{{_batch_id}}", batchId.ToString())
                .Replace("{{_batch_start_time}}", (batchStartTime ?? DateTime.UtcNow).ToString("o"));
            resolved[key] = value;
        }
        return resolved;
    }
}
