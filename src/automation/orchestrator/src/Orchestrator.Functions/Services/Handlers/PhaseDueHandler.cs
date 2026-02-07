using System.Text.Json;
using Microsoft.Extensions.Logging;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;

namespace Orchestrator.Functions.Services.Handlers;

public interface IPhaseDueHandler
{
    Task HandleAsync(PhaseDueMessage message);
}

public class PhaseDueHandler : IPhaseDueHandler
{
    private readonly IBatchRepository _batchRepo;
    private readonly IRunbookRepository _runbookRepo;
    private readonly IPhaseExecutionRepository _phaseRepo;
    private readonly IStepExecutionRepository _stepRepo;
    private readonly IMemberRepository _memberRepo;
    private readonly IWorkerDispatcher _workerDispatcher;
    private readonly IRunbookParser _runbookParser;
    private readonly ILogger<PhaseDueHandler> _logger;

    public PhaseDueHandler(
        IBatchRepository batchRepo,
        IRunbookRepository runbookRepo,
        IPhaseExecutionRepository phaseRepo,
        IStepExecutionRepository stepRepo,
        IMemberRepository memberRepo,
        IWorkerDispatcher workerDispatcher,
        IRunbookParser runbookParser,
        ILogger<PhaseDueHandler> logger)
    {
        _batchRepo = batchRepo;
        _runbookRepo = runbookRepo;
        _phaseRepo = phaseRepo;
        _stepRepo = stepRepo;
        _memberRepo = memberRepo;
        _workerDispatcher = workerDispatcher;
        _runbookParser = runbookParser;
        _logger = logger;
    }

    public async Task HandleAsync(PhaseDueMessage message)
    {
        _logger.LogInformation(
            "Processing phase-due for phase {PhaseExecutionId} ({PhaseName}) in batch {BatchId}",
            message.PhaseExecutionId, message.PhaseName, message.BatchId);

        var phaseExecution = await _phaseRepo.GetByIdAsync(message.PhaseExecutionId);
        if (phaseExecution == null)
        {
            _logger.LogError("Phase execution {PhaseExecutionId} not found", message.PhaseExecutionId);
            return;
        }

        // Get all step executions for this phase grouped by step_index
        var allSteps = (await _stepRepo.GetByPhaseExecutionAsync(message.PhaseExecutionId))
            .OrderBy(s => s.StepIndex)
            .ThenBy(s => s.Id)
            .ToList();

        if (allSteps.Count == 0)
        {
            _logger.LogInformation(
                "No step executions for phase {PhaseExecutionId}, marking complete",
                message.PhaseExecutionId);
            await _phaseRepo.SetCompletedAsync(message.PhaseExecutionId);
            await CheckBatchCompletionAsync(message.BatchId);
            return;
        }

        // Group steps by step_index
        var stepGroups = allSteps.GroupBy(s => s.StepIndex).OrderBy(g => g.Key).ToList();

        // Find the first step_index that has pending steps
        foreach (var group in stepGroups)
        {
            var stepIndex = group.Key;
            var stepsAtIndex = group.ToList();

            // Check if there are any pending steps at this index
            var pendingSteps = stepsAtIndex.Where(s => s.Status == StepStatus.Pending).ToList();
            if (pendingSteps.Count == 0)
            {
                // All steps at this index are dispatched/completed/failed
                // Check if any are still in progress
                var inProgressSteps = stepsAtIndex.Where(s => s.Status == StepStatus.Dispatched || s.Status == StepStatus.Polling).ToList();
                if (inProgressSteps.Count > 0)
                {
                    _logger.LogInformation(
                        "Waiting for {Count} in-progress steps at index {StepIndex} for phase {PhaseExecutionId}",
                        inProgressSteps.Count, stepIndex, message.PhaseExecutionId);
                    return; // Wait for them to complete before proceeding to next index
                }

                // Check if any failed
                var failedSteps = stepsAtIndex.Where(s => s.Status == StepStatus.Failed || s.Status == StepStatus.PollTimeout).ToList();
                if (failedSteps.Count > 0)
                {
                    _logger.LogWarning(
                        "{Count} steps failed at index {StepIndex} for phase {PhaseExecutionId}",
                        failedSteps.Count, stepIndex, message.PhaseExecutionId);
                    // Continue to check for rollback handling in ResultProcessor
                }

                continue; // Move to next index
            }

            // Dispatch all pending steps at this index in parallel
            _logger.LogInformation(
                "Dispatching {Count} steps at index {StepIndex} for phase {PhaseExecutionId}",
                pendingSteps.Count, stepIndex, message.PhaseExecutionId);

            var jobs = new List<WorkerJobMessage>();
            foreach (var step in pendingSteps)
            {
                var job = new WorkerJobMessage
                {
                    JobId = $"step-{step.Id}",
                    BatchId = message.BatchId,
                    WorkerId = step.WorkerId!,
                    FunctionName = step.FunctionName!,
                    Parameters = string.IsNullOrEmpty(step.ParamsJson)
                        ? new Dictionary<string, string>()
                        : JsonSerializer.Deserialize<Dictionary<string, string>>(step.ParamsJson) ?? new(),
                    CorrelationData = new JobCorrelationData
                    {
                        StepExecutionId = step.Id,
                        IsInitStep = false,
                        RunbookName = message.RunbookName,
                        RunbookVersion = message.RunbookVersion
                    }
                };
                jobs.Add(job);
            }

            await _workerDispatcher.DispatchJobsAsync(jobs);

            // Update step statuses
            foreach (var (step, job) in pendingSteps.Zip(jobs))
            {
                await _stepRepo.SetDispatchedAsync(step.Id, job.JobId);
            }

            return; // Only dispatch one step_index at a time
        }

        // All step indices processed, phase is complete
        _logger.LogInformation(
            "All steps completed for phase {PhaseExecutionId}, marking complete",
            message.PhaseExecutionId);
        await _phaseRepo.SetCompletedAsync(message.PhaseExecutionId);
        await CheckBatchCompletionAsync(message.BatchId);
    }

    private async Task CheckBatchCompletionAsync(int batchId)
    {
        var phases = await _phaseRepo.GetByBatchAsync(batchId);
        var allCompleted = phases.All(p => p.Status == PhaseStatus.Completed || p.Status == PhaseStatus.Skipped);
        var anyFailed = phases.Any(p => p.Status == PhaseStatus.Failed);

        if (anyFailed)
        {
            _logger.LogWarning("Batch {BatchId} has failed phases", batchId);
            await _batchRepo.SetFailedAsync(batchId);
        }
        else if (allCompleted)
        {
            _logger.LogInformation("All phases completed for batch {BatchId}, marking batch complete", batchId);
            await _batchRepo.SetCompletedAsync(batchId);
        }
    }
}
