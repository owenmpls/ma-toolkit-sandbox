using System.Text.Json;
using Microsoft.Extensions.Logging;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;

namespace Orchestrator.Functions.Services;

public interface IPhaseProgressionService
{
    Task CheckMemberProgressionAsync(int phaseExecutionId, int batchMemberId, string runbookName, int runbookVersion);
    Task HandleMemberFailureAsync(int phaseExecutionId, int batchMemberId);
    Task CheckPhaseCompletionAsync(int phaseExecutionId);
    Task CheckBatchCompletionAsync(int batchId);
}

public class PhaseProgressionService : IPhaseProgressionService
{
    private readonly IStepExecutionRepository _stepRepo;
    private readonly IPhaseExecutionRepository _phaseRepo;
    private readonly IBatchRepository _batchRepo;
    private readonly IMemberRepository _memberRepo;
    private readonly IWorkerDispatcher _workerDispatcher;
    private readonly IRunbookRepository _runbookRepo;
    private readonly IRunbookParser _runbookParser;
    private readonly ITemplateResolver _templateResolver;
    private readonly ILogger<PhaseProgressionService> _logger;

    public PhaseProgressionService(
        IStepExecutionRepository stepRepo,
        IPhaseExecutionRepository phaseRepo,
        IBatchRepository batchRepo,
        IMemberRepository memberRepo,
        IWorkerDispatcher workerDispatcher,
        IRunbookRepository runbookRepo,
        IRunbookParser runbookParser,
        ITemplateResolver templateResolver,
        ILogger<PhaseProgressionService> logger)
    {
        _stepRepo = stepRepo;
        _phaseRepo = phaseRepo;
        _batchRepo = batchRepo;
        _memberRepo = memberRepo;
        _workerDispatcher = workerDispatcher;
        _runbookRepo = runbookRepo;
        _runbookParser = runbookParser;
        _templateResolver = templateResolver;
        _logger = logger;
    }

    public async Task CheckMemberProgressionAsync(int phaseExecutionId, int batchMemberId, string runbookName, int runbookVersion)
    {
        var memberSteps = (await _stepRepo.GetByPhaseAndMemberAsync(phaseExecutionId, batchMemberId))
            .OrderBy(s => s.StepIndex)
            .ToList();

        if (memberSteps.Count == 0)
        {
            await CheckPhaseCompletionAsync(phaseExecutionId);
            return;
        }

        // Walk through this member's steps in order
        foreach (var step in memberSteps)
        {
            switch (step.Status)
            {
                case StepStatus.Pending:
                    // Dispatch this step — re-resolve params from fresh member data
                    var phase = await _phaseRepo.GetByIdAsync(phaseExecutionId);
                    var batchId = phase?.BatchId ?? 0;

                    var resolvedParams = string.IsNullOrEmpty(step.ParamsJson)
                        ? new Dictionary<string, string>()
                        : JsonSerializer.Deserialize<Dictionary<string, string>>(step.ParamsJson) ?? new();
                    var resolvedFunction = step.FunctionName!;

                    // Re-resolve templates from merged data_json + worker_data_json
                    try
                    {
                        var member = await _memberRepo.GetByIdAsync(batchMemberId);
                        var runbook = await _runbookRepo.GetByNameAndVersionAsync(runbookName, runbookVersion);
                        if (member != null && runbook != null && !string.IsNullOrEmpty(member.DataJson))
                        {
                            var definition = _runbookParser.Parse(runbook.YamlContent);
                            var phaseDef = definition.Phases.FirstOrDefault(p => p.Name == phase?.PhaseName);
                            var stepDef = phaseDef?.Steps.FirstOrDefault(s => s.Name == step.StepName);

                            if (stepDef != null)
                            {
                                var mergedData = JsonSerializer.Deserialize<Dictionary<string, string>>(member.DataJson)!;
                                if (!string.IsNullOrEmpty(member.WorkerDataJson))
                                {
                                    var workerData = JsonSerializer.Deserialize<Dictionary<string, string>>(member.WorkerDataJson)!;
                                    foreach (var (key, value) in workerData)
                                        mergedData[key] = value;
                                }

                                var batch = await _batchRepo.GetByIdAsync(batchId);
                                resolvedParams = _templateResolver.ResolveParams(
                                    stepDef.Params, mergedData, batchId, batch?.BatchStartTime);
                                resolvedFunction = _templateResolver.ResolveString(
                                    stepDef.Function, mergedData, batchId, batch?.BatchStartTime);

                                await _stepRepo.UpdateParamsJsonAsync(step.Id, JsonSerializer.Serialize(resolvedParams));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to re-resolve params for step '{StepName}' member {MemberId}, using pre-resolved params",
                            step.StepName, batchMemberId);
                    }

                    var job = new WorkerJobMessage
                    {
                        JobId = $"step-{step.Id}",
                        BatchId = batchId,
                        WorkerId = step.WorkerId!,
                        FunctionName = resolvedFunction,
                        Parameters = resolvedParams,
                        CorrelationData = new JobCorrelationData
                        {
                            StepExecutionId = step.Id,
                            IsInitStep = false,
                            RunbookName = runbookName,
                            RunbookVersion = runbookVersion
                        }
                    };

                    await _workerDispatcher.DispatchJobAsync(job);
                    await _stepRepo.SetDispatchedAsync(step.Id, job.JobId);

                    _logger.LogInformation(
                        "Dispatched step '{StepName}' (index {StepIndex}) for member {MemberId} in phase {PhaseExecutionId}",
                        step.StepName, step.StepIndex, batchMemberId, phaseExecutionId);
                    return;

                case StepStatus.Dispatched:
                case StepStatus.Polling:
                    // Still in progress — wait
                    return;

                case StepStatus.Failed:
                case StepStatus.PollTimeout:
                case StepStatus.Cancelled:
                    // Member already handled or step cancelled — stop
                    return;

                case StepStatus.Succeeded:
                    // Continue to next step
                    continue;

                default:
                    // Unknown status — don't proceed
                    _logger.LogWarning(
                        "Step {StepId} has unexpected status '{Status}'",
                        step.Id, step.Status);
                    return;
            }
        }

        // All steps succeeded for this member in this phase
        _logger.LogInformation(
            "All steps completed for member {MemberId} in phase {PhaseExecutionId}",
            batchMemberId, phaseExecutionId);

        await CheckPhaseCompletionAsync(phaseExecutionId);
    }

    public async Task HandleMemberFailureAsync(int phaseExecutionId, int batchMemberId)
    {
        // Mark member as failed (idempotent via WHERE status = 'active')
        var marked = await _memberRepo.SetFailedAsync(batchMemberId);
        if (marked)
        {
            _logger.LogWarning(
                "Member {MemberId} marked as failed",
                batchMemberId);
        }

        // Cancel all non-terminal steps for this member across ALL phases
        var pendingSteps = await _stepRepo.GetPendingByMemberAsync(batchMemberId);
        foreach (var step in pendingSteps)
        {
            await _stepRepo.SetCancelledAsync(step.Id);
            _logger.LogInformation(
                "Cancelled step {StepId} ('{StepName}') for failed member {MemberId}",
                step.Id, step.StepName, batchMemberId);
        }

        await CheckPhaseCompletionAsync(phaseExecutionId);
    }

    public async Task CheckPhaseCompletionAsync(int phaseExecutionId)
    {
        var allSteps = (await _stepRepo.GetByPhaseExecutionAsync(phaseExecutionId)).ToList();

        if (allSteps.Count == 0)
        {
            // Empty phase — mark completed
            if (await _phaseRepo.SetCompletedAsync(phaseExecutionId))
            {
                _logger.LogInformation("Empty phase {PhaseExecutionId} marked completed", phaseExecutionId);
                var phase = await _phaseRepo.GetByIdAsync(phaseExecutionId);
                if (phase != null)
                    await CheckBatchCompletionAsync(phase.BatchId);
            }
            return;
        }

        // Check if any step is still in progress
        if (allSteps.Any(s => s.Status is StepStatus.Pending or StepStatus.Dispatched or StepStatus.Polling))
            return;

        // All steps are terminal — determine phase outcome
        var memberGroups = allSteps.GroupBy(s => s.BatchMemberId);
        var anyMemberFullySucceeded = false;

        foreach (var group in memberGroups)
        {
            if (group.All(s => s.Status == StepStatus.Succeeded))
            {
                anyMemberFullySucceeded = true;
                break;
            }
        }

        var phase2 = await _phaseRepo.GetByIdAsync(phaseExecutionId);
        if (phase2 == null) return;

        if (anyMemberFullySucceeded)
        {
            if (await _phaseRepo.SetCompletedAsync(phaseExecutionId))
            {
                _logger.LogInformation(
                    "Phase {PhaseExecutionId} completed (some members succeeded)",
                    phaseExecutionId);
            }
        }
        else
        {
            if (await _phaseRepo.SetFailedAsync(phaseExecutionId))
            {
                _logger.LogWarning(
                    "Phase {PhaseExecutionId} failed (no members fully succeeded)",
                    phaseExecutionId);
            }
        }

        await CheckBatchCompletionAsync(phase2.BatchId);
    }

    public async Task CheckBatchCompletionAsync(int batchId)
    {
        var phases = (await _phaseRepo.GetByBatchAsync(batchId)).ToList();

        // If any phase is still pending or in-progress, batch is not done
        if (phases.Any(p => p.Status is PhaseStatus.Pending or PhaseStatus.Dispatched))
            return;

        // All phases are terminal
        var anyCompleted = phases.Any(p => p.Status == PhaseStatus.Completed);

        if (anyCompleted)
        {
            _logger.LogInformation("All phases terminal for batch {BatchId}, marking batch completed", batchId);
            await _batchRepo.SetCompletedAsync(batchId);
        }
        else
        {
            // All phases failed/skipped/superseded — batch failed
            _logger.LogWarning("All phases terminal for batch {BatchId} with no completions, marking batch failed", batchId);
            await _batchRepo.SetFailedAsync(batchId);
        }
    }
}
