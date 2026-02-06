using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orchestrator.Functions.Models.Db;
using Orchestrator.Functions.Models.Messages;
using Orchestrator.Functions.Services.Repositories;

namespace Orchestrator.Functions.Services.Handlers;

public interface IResultProcessor
{
    Task ProcessAsync(WorkerResultMessage result);
}

public class ResultProcessor : IResultProcessor
{
    private readonly IStepExecutionRepository _stepRepo;
    private readonly IInitExecutionRepository _initRepo;
    private readonly IBatchRepository _batchRepo;
    private readonly IPhaseExecutionRepository _phaseRepo;
    private readonly IRunbookRepository _runbookRepo;
    private readonly IRunbookParser _runbookParser;
    private readonly IWorkerDispatcher _workerDispatcher;
    private readonly IRollbackExecutor _rollbackExecutor;
    private readonly IDynamicTableReader _dynamicTableReader;
    private readonly ILogger<ResultProcessor> _logger;

    public ResultProcessor(
        IStepExecutionRepository stepRepo,
        IInitExecutionRepository initRepo,
        IBatchRepository batchRepo,
        IPhaseExecutionRepository phaseRepo,
        IRunbookRepository runbookRepo,
        IRunbookParser runbookParser,
        IWorkerDispatcher workerDispatcher,
        IRollbackExecutor rollbackExecutor,
        IDynamicTableReader dynamicTableReader,
        ILogger<ResultProcessor> logger)
    {
        _stepRepo = stepRepo;
        _initRepo = initRepo;
        _batchRepo = batchRepo;
        _phaseRepo = phaseRepo;
        _runbookRepo = runbookRepo;
        _runbookParser = runbookParser;
        _workerDispatcher = workerDispatcher;
        _rollbackExecutor = rollbackExecutor;
        _dynamicTableReader = dynamicTableReader;
        _logger = logger;
    }

    public async Task ProcessAsync(WorkerResultMessage result)
    {
        _logger.LogInformation(
            "Processing worker result for job {JobId}: Status={Status}, DurationMs={DurationMs}",
            result.JobId, result.Status, result.DurationMs);

        var correlation = result.CorrelationData;
        if (correlation == null)
        {
            _logger.LogWarning("No correlation data in result for job {JobId}", result.JobId);
            return;
        }

        if (correlation.IsInitStep && correlation.InitExecutionId.HasValue)
        {
            await ProcessInitResultAsync(result, correlation);
        }
        else if (correlation.StepExecutionId.HasValue)
        {
            await ProcessStepResultAsync(result, correlation);
        }
        else
        {
            _logger.LogWarning("Invalid correlation data in result for job {JobId}", result.JobId);
        }
    }

    private async Task ProcessInitResultAsync(WorkerResultMessage result, JobCorrelationData correlation)
    {
        var initExec = await _initRepo.GetByIdAsync(correlation.InitExecutionId!.Value);
        if (initExec == null)
        {
            _logger.LogWarning("Init execution {InitExecutionId} not found", correlation.InitExecutionId);
            return;
        }

        var resultJson = result.Result.HasValue
            ? JsonSerializer.Serialize(result.Result.Value)
            : null;

        if (result.Status == "Success")
        {
            // Check if this is a polling response
            if (result.IsPollingInProgress())
            {
                _logger.LogInformation(
                    "Init execution {InitExecutionId} is polling (still in progress)",
                    initExec.Id);
                await _initRepo.SetPollingAsync(initExec.Id);
                return;
            }

            // Success - mark completed
            var finalResult = result.GetPollingResultData();
            var finalResultJson = finalResult.HasValue
                ? JsonSerializer.Serialize(finalResult.Value)
                : resultJson;

            await _initRepo.SetSucceededAsync(initExec.Id, finalResultJson);

            _logger.LogInformation(
                "Init execution {InitExecutionId} succeeded for batch {BatchId}",
                initExec.Id, initExec.BatchId);

            // Check if there are more init steps to dispatch
            await DispatchNextInitStepAsync(initExec.BatchId, correlation.RunbookName, correlation.RunbookVersion);
        }
        else
        {
            // Failure
            var errorMessage = result.Error?.Message ?? "Unknown error";
            await _initRepo.SetFailedAsync(initExec.Id, errorMessage);

            _logger.LogError(
                "Init execution {InitExecutionId} failed for batch {BatchId}: {Error}",
                initExec.Id, initExec.BatchId, errorMessage);

            // Mark batch as failed
            await _batchRepo.SetFailedAsync(initExec.BatchId);

            // Trigger rollback if configured
            if (!string.IsNullOrEmpty(initExec.OnFailure))
            {
                await TriggerRollbackAsync(initExec.OnFailure, correlation.RunbookName, correlation.RunbookVersion, initExec.BatchId, null, null);
            }
        }
    }

    private async Task ProcessStepResultAsync(WorkerResultMessage result, JobCorrelationData correlation)
    {
        var stepExec = await _stepRepo.GetByIdAsync(correlation.StepExecutionId!.Value);
        if (stepExec == null)
        {
            _logger.LogWarning("Step execution {StepExecutionId} not found", correlation.StepExecutionId);
            return;
        }

        var resultJson = result.Result.HasValue
            ? JsonSerializer.Serialize(result.Result.Value)
            : null;

        if (result.Status == "Success")
        {
            // Check if this is a polling response
            if (result.IsPollingInProgress())
            {
                _logger.LogInformation(
                    "Step execution {StepExecutionId} is polling (still in progress)",
                    stepExec.Id);
                await _stepRepo.SetPollingAsync(stepExec.Id);
                return;
            }

            // Success - mark completed
            var finalResult = result.GetPollingResultData();
            var finalResultJson = finalResult.HasValue
                ? JsonSerializer.Serialize(finalResult.Value)
                : resultJson;

            await _stepRepo.SetSucceededAsync(stepExec.Id, finalResultJson);

            _logger.LogInformation(
                "Step execution {StepExecutionId} succeeded",
                stepExec.Id);

            // Check if we need to advance to next step_index or complete the phase
            await CheckPhaseProgressionAsync(stepExec.PhaseExecutionId, correlation.RunbookName, correlation.RunbookVersion);
        }
        else
        {
            // Failure
            var errorMessage = result.Error?.Message ?? "Unknown error";
            await _stepRepo.SetFailedAsync(stepExec.Id, errorMessage);

            _logger.LogError(
                "Step execution {StepExecutionId} failed: {Error}",
                stepExec.Id, errorMessage);

            // Trigger rollback if configured
            if (!string.IsNullOrEmpty(stepExec.OnFailure))
            {
                // Get phase execution to find batch ID
                var phaseExec = await _phaseRepo.GetByIdAsync(stepExec.PhaseExecutionId);
                if (phaseExec != null)
                {
                    await TriggerRollbackAsync(stepExec.OnFailure, correlation.RunbookName, correlation.RunbookVersion,
                        phaseExec.BatchId, stepExec.BatchMemberId, null);
                }
            }
        }
    }

    private async Task DispatchNextInitStepAsync(int batchId, string runbookName, int runbookVersion)
    {
        var pendingSteps = (await _initRepo.GetPendingByBatchAsync(batchId))
            .OrderBy(s => s.StepIndex)
            .ToList();

        if (pendingSteps.Count == 0)
        {
            // All init steps completed - activate batch
            _logger.LogInformation("All init steps completed for batch {BatchId}, setting to active", batchId);
            await _batchRepo.SetActiveAsync(batchId);
            return;
        }

        // Dispatch next init step
        var nextStep = pendingSteps.First();
        var job = new WorkerJobMessage
        {
            JobId = Guid.NewGuid().ToString(),
            BatchId = batchId,
            WorkerId = nextStep.WorkerId!,
            FunctionName = nextStep.FunctionName!,
            Parameters = string.IsNullOrEmpty(nextStep.ParamsJson)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(nextStep.ParamsJson) ?? new(),
            CorrelationData = new JobCorrelationData
            {
                InitExecutionId = nextStep.Id,
                IsInitStep = true,
                RunbookName = runbookName,
                RunbookVersion = runbookVersion
            }
        };

        await _workerDispatcher.DispatchJobAsync(job);
        await _initRepo.SetDispatchedAsync(nextStep.Id, job.JobId);

        _logger.LogInformation(
            "Dispatched next init step '{StepName}' (job {JobId}) for batch {BatchId}",
            nextStep.StepName, job.JobId, batchId);
    }

    private async Task CheckPhaseProgressionAsync(int phaseExecutionId, string runbookName, int runbookVersion)
    {
        var allSteps = (await _stepRepo.GetByPhaseExecutionAsync(phaseExecutionId))
            .OrderBy(s => s.StepIndex)
            .ThenBy(s => s.Id)
            .ToList();

        // Group by step_index
        var stepGroups = allSteps.GroupBy(s => s.StepIndex).OrderBy(g => g.Key).ToList();

        foreach (var group in stepGroups)
        {
            var stepIndex = group.Key;
            var stepsAtIndex = group.ToList();

            var pendingSteps = stepsAtIndex.Where(s => s.Status == "pending").ToList();
            var inProgressSteps = stepsAtIndex.Where(s => s.Status == "dispatched" || s.Status == "polling").ToList();
            var succeededSteps = stepsAtIndex.Where(s => s.Status == "succeeded").ToList();

            // If there are pending steps at this index, dispatch them
            if (pendingSteps.Count > 0)
            {
                _logger.LogInformation(
                    "Dispatching {Count} pending steps at index {StepIndex} for phase {PhaseExecutionId}",
                    pendingSteps.Count, stepIndex, phaseExecutionId);

                foreach (var step in pendingSteps)
                {
                    var job = new WorkerJobMessage
                    {
                        JobId = Guid.NewGuid().ToString(),
                        BatchId = 0, // Will be set from phase
                        WorkerId = step.WorkerId!,
                        FunctionName = step.FunctionName!,
                        Parameters = string.IsNullOrEmpty(step.ParamsJson)
                            ? new Dictionary<string, string>()
                            : JsonSerializer.Deserialize<Dictionary<string, string>>(step.ParamsJson) ?? new(),
                        CorrelationData = new JobCorrelationData
                        {
                            StepExecutionId = step.Id,
                            IsInitStep = false,
                            RunbookName = runbookName,
                            RunbookVersion = runbookVersion
                        }
                    };

                    // Get batch ID from phase
                    var phase = await _phaseRepo.GetByIdAsync(phaseExecutionId);
                    if (phase != null)
                    {
                        job.BatchId = phase.BatchId;
                    }

                    await _workerDispatcher.DispatchJobAsync(job);
                    await _stepRepo.SetDispatchedAsync(step.Id, job.JobId);
                }
                return;
            }

            // If there are in-progress steps at this index, wait
            if (inProgressSteps.Count > 0)
            {
                _logger.LogDebug(
                    "Waiting for {Count} in-progress steps at index {StepIndex} for phase {PhaseExecutionId}",
                    inProgressSteps.Count, stepIndex, phaseExecutionId);
                return;
            }

            // If not all steps at this index succeeded, there was a failure - don't proceed
            if (succeededSteps.Count != stepsAtIndex.Count)
            {
                _logger.LogWarning(
                    "Not all steps succeeded at index {StepIndex} for phase {PhaseExecutionId} ({Succeeded}/{Total})",
                    stepIndex, phaseExecutionId, succeededSteps.Count, stepsAtIndex.Count);
                // Don't mark phase as failed here - individual step handlers should handle that
                return;
            }

            // All steps at this index succeeded, continue to next index
        }

        // All steps completed - mark phase as completed
        _logger.LogInformation("All steps completed for phase {PhaseExecutionId}", phaseExecutionId);
        await _phaseRepo.SetCompletedAsync(phaseExecutionId);

        // Check if batch is complete
        var phaseExec = await _phaseRepo.GetByIdAsync(phaseExecutionId);
        if (phaseExec != null)
        {
            await CheckBatchCompletionAsync(phaseExec.BatchId);
        }
    }

    private async Task CheckBatchCompletionAsync(int batchId)
    {
        var phases = await _phaseRepo.GetByBatchAsync(batchId);
        var allCompleted = phases.All(p => p.Status == "completed" || p.Status == "skipped");
        var anyFailed = phases.Any(p => p.Status == "failed");

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

    private async Task TriggerRollbackAsync(string rollbackName, string runbookName, int runbookVersion, int batchId, int? batchMemberId, System.Data.DataRow? memberData)
    {
        var runbook = await _runbookRepo.GetByNameAndVersionAsync(runbookName, runbookVersion);
        if (runbook == null)
        {
            _logger.LogWarning("Runbook {RunbookName} v{RunbookVersion} not found for rollback",
                runbookName, runbookVersion);
            return;
        }

        var definition = _runbookParser.Parse(runbook.YamlContent);
        var batch = await _batchRepo.GetByIdAsync(batchId);
        if (batch == null)
        {
            _logger.LogWarning("Batch {BatchId} not found for rollback", batchId);
            return;
        }

        await _rollbackExecutor.ExecuteRollbackAsync(rollbackName, definition, batch, memberData, batchMemberId);
    }
}
