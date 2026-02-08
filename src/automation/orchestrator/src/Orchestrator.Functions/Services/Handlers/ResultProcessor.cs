using System.Text.Json;
using Microsoft.Extensions.Logging;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;

namespace Orchestrator.Functions.Services.Handlers;

public interface IResultProcessor
{
    Task<bool> ProcessAsync(WorkerResultMessage result);
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
    private readonly IMemberRepository _memberRepo;
    private readonly IPhaseProgressionService _progressionService;
    private readonly IRetryScheduler _retryScheduler;
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
        IMemberRepository memberRepo,
        IPhaseProgressionService progressionService,
        IRetryScheduler retryScheduler,
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
        _memberRepo = memberRepo;
        _progressionService = progressionService;
        _retryScheduler = retryScheduler;
        _logger = logger;
    }

    public async Task<bool> ProcessAsync(WorkerResultMessage result)
    {
        _logger.LogInformation(
            "Processing worker result for job {JobId}: Status={Status}, DurationMs={DurationMs}",
            result.JobId, result.Status, result.DurationMs);

        var correlation = result.CorrelationData!;

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
            _logger.LogWarning("Invalid correlation data in result for job {JobId}: missing both execution IDs", result.JobId);
            return false;
        }

        return true;
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

        if (result.Status == WorkerResultStatus.Success)
        {
            // Check if this is a polling response
            if (result.IsPollingInProgress())
            {
                _logger.LogInformation(
                    "Init execution {InitExecutionId} is polling (still in progress)",
                    initExec.Id);
                if (!await _initRepo.SetPollingAsync(initExec.Id))
                {
                    _logger.LogWarning("Init execution {InitExecutionId} was already updated by another handler", initExec.Id);
                    return;
                }
                return;
            }

            // Success - mark completed
            var finalResult = result.GetPollingResultData();
            var finalResultJson = finalResult.HasValue
                ? JsonSerializer.Serialize(finalResult.Value)
                : resultJson;

            if (!await _initRepo.SetSucceededAsync(initExec.Id, finalResultJson))
            {
                _logger.LogWarning("Init execution {InitExecutionId} was already updated by another handler", initExec.Id);
                return;
            }

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
            if (!await _initRepo.SetFailedAsync(initExec.Id, errorMessage))
            {
                _logger.LogWarning("Init execution {InitExecutionId} was already updated by another handler", initExec.Id);
                return;
            }

            _logger.LogError(
                "Init execution {InitExecutionId} failed for batch {BatchId}: {Error}",
                initExec.Id, initExec.BatchId, errorMessage);

            // Check if retries remain
            if (initExec.MaxRetries.HasValue && initExec.RetryCount < initExec.MaxRetries.Value)
            {
                var retryAfter = DateTime.UtcNow.AddSeconds(initExec.RetryIntervalSec ?? 0);
                if (await _initRepo.SetRetryPendingAsync(initExec.Id, retryAfter))
                {
                    _logger.LogWarning(
                        "Init execution {InitExecutionId} failed, scheduling retry {RetryCount}/{MaxRetries} after {RetryAfter}",
                        initExec.Id, initExec.RetryCount + 1, initExec.MaxRetries, retryAfter);

                    await _retryScheduler.ScheduleRetryAsync(new RetryCheckMessage
                    {
                        StepExecutionId = initExec.Id,
                        IsInitStep = true,
                        RunbookName = correlation.RunbookName,
                        RunbookVersion = correlation.RunbookVersion,
                        BatchId = initExec.BatchId
                    }, TimeSpan.FromSeconds(initExec.RetryIntervalSec ?? 0));

                    return;
                }
            }

            // Mark batch as failed
            await _batchRepo.SetFailedAsync(initExec.BatchId);

            // Trigger rollback if configured
            if (!string.IsNullOrEmpty(initExec.OnFailure))
            {
                await TriggerRollbackAsync(initExec.OnFailure, correlation.RunbookName, correlation.RunbookVersion, initExec.BatchId, null);
            }
        }
    }

    private static bool IsTerminalStepStatus(string status) =>
        status is StepStatus.Succeeded or StepStatus.Failed or StepStatus.PollTimeout or StepStatus.Cancelled;

    private async Task ProcessStepResultAsync(WorkerResultMessage result, JobCorrelationData correlation)
    {
        var stepExec = await _stepRepo.GetByIdAsync(correlation.StepExecutionId!.Value);
        if (stepExec == null)
        {
            _logger.LogWarning("Step execution {StepExecutionId} not found", correlation.StepExecutionId);
            return;
        }

        // Guard: if step is already terminal, ignore this result (e.g. result for a cancelled step)
        if (IsTerminalStepStatus(stepExec.Status))
        {
            _logger.LogInformation(
                "Step execution {StepExecutionId} already in terminal status '{Status}', ignoring result",
                stepExec.Id, stepExec.Status);
            return;
        }

        var resultJson = result.Result.HasValue
            ? JsonSerializer.Serialize(result.Result.Value)
            : null;

        if (result.Status == WorkerResultStatus.Success)
        {
            // Check if this is a polling response
            if (result.IsPollingInProgress())
            {
                _logger.LogInformation(
                    "Step execution {StepExecutionId} is polling (still in progress)",
                    stepExec.Id);
                if (!await _stepRepo.SetPollingAsync(stepExec.Id))
                {
                    _logger.LogWarning("Step execution {StepExecutionId} was already updated by another handler", stepExec.Id);
                    return;
                }
                return;
            }

            // Success - mark completed
            var finalResult = result.GetPollingResultData();
            var finalResultJson = finalResult.HasValue
                ? JsonSerializer.Serialize(finalResult.Value)
                : resultJson;

            if (!await _stepRepo.SetSucceededAsync(stepExec.Id, finalResultJson))
            {
                _logger.LogWarning("Step execution {StepExecutionId} was already updated by another handler", stepExec.Id);
                return;
            }

            _logger.LogInformation(
                "Step execution {StepExecutionId} succeeded",
                stepExec.Id);

            // Extract output params if configured
            await ExtractOutputParamsAsync(stepExec, finalResultJson, correlation);

            // Advance this member to the next step (per-member progression)
            await _progressionService.CheckMemberProgressionAsync(
                stepExec.PhaseExecutionId, stepExec.BatchMemberId,
                correlation.RunbookName, correlation.RunbookVersion);
        }
        else
        {
            // Failure
            var errorMessage = result.Error?.Message ?? "Unknown error";
            if (!await _stepRepo.SetFailedAsync(stepExec.Id, errorMessage))
            {
                _logger.LogWarning("Step execution {StepExecutionId} was already updated by another handler", stepExec.Id);
                return;
            }

            _logger.LogError(
                "Step execution {StepExecutionId} failed: {Error}",
                stepExec.Id, errorMessage);

            // Check if retries remain
            if (stepExec.MaxRetries.HasValue && stepExec.RetryCount < stepExec.MaxRetries.Value)
            {
                var retryAfter = DateTime.UtcNow.AddSeconds(stepExec.RetryIntervalSec ?? 0);
                if (await _stepRepo.SetRetryPendingAsync(stepExec.Id, retryAfter))
                {
                    _logger.LogWarning(
                        "Step execution {StepExecutionId} failed, scheduling retry {RetryCount}/{MaxRetries} after {RetryAfter}",
                        stepExec.Id, stepExec.RetryCount + 1, stepExec.MaxRetries, retryAfter);

                    var phaseExec = await _phaseRepo.GetByIdAsync(stepExec.PhaseExecutionId);
                    await _retryScheduler.ScheduleRetryAsync(new RetryCheckMessage
                    {
                        StepExecutionId = stepExec.Id,
                        IsInitStep = false,
                        RunbookName = correlation.RunbookName,
                        RunbookVersion = correlation.RunbookVersion,
                        BatchId = phaseExec?.BatchId ?? 0
                    }, TimeSpan.FromSeconds(stepExec.RetryIntervalSec ?? 0));

                    return;
                }
            }

            // Trigger rollback if configured
            if (!string.IsNullOrEmpty(stepExec.OnFailure))
            {
                var phaseExec = await _phaseRepo.GetByIdAsync(stepExec.PhaseExecutionId);
                if (phaseExec != null)
                {
                    await TriggerRollbackAsync(stepExec.OnFailure, correlation.RunbookName, correlation.RunbookVersion,
                        phaseExec.BatchId, stepExec.BatchMemberId);
                }
            }

            // Handle member failure: mark member failed, cancel remaining steps, check phase completion
            await _progressionService.HandleMemberFailureAsync(
                stepExec.PhaseExecutionId, stepExec.BatchMemberId);
        }
    }

    private async Task ExtractOutputParamsAsync(
        StepExecutionRecord stepExec, string? finalResultJson, JobCorrelationData correlation)
    {
        if (string.IsNullOrEmpty(finalResultJson))
            return;

        try
        {
            var runbook = await _runbookRepo.GetByNameAndVersionAsync(
                correlation.RunbookName, correlation.RunbookVersion);
            if (runbook == null)
            {
                _logger.LogWarning(
                    "Runbook {RunbookName} v{Version} not found for output param extraction",
                    correlation.RunbookName, correlation.RunbookVersion);
                return;
            }

            var definition = _runbookParser.Parse(runbook.YamlContent);

            // Find the phase execution to get phase name
            var phaseExec = await _phaseRepo.GetByIdAsync(stepExec.PhaseExecutionId);
            if (phaseExec == null)
                return;

            var phaseDef = definition.Phases.FirstOrDefault(p => p.Name == phaseExec.PhaseName);
            if (phaseDef == null)
                return;

            var stepDef = phaseDef.Steps.FirstOrDefault(s => s.Name == stepExec.StepName);
            if (stepDef == null || stepDef.OutputParams.Count == 0)
                return;

            var resultDoc = JsonDocument.Parse(finalResultJson);
            var outputDict = new Dictionary<string, string>();

            foreach (var (outputKey, resultFieldName) in stepDef.OutputParams)
            {
                if (resultDoc.RootElement.TryGetProperty(resultFieldName, out var fieldValue))
                {
                    outputDict[outputKey] = fieldValue.ValueKind == JsonValueKind.String
                        ? fieldValue.GetString()!
                        : fieldValue.GetRawText();
                }
                else
                {
                    _logger.LogWarning(
                        "Output param '{OutputKey}': result field '{ResultField}' not found in step '{StepName}' result for member {MemberId}",
                        outputKey, resultFieldName, stepExec.StepName, stepExec.BatchMemberId);
                }
            }

            if (outputDict.Count > 0)
            {
                await _memberRepo.MergeWorkerDataAsync(stepExec.BatchMemberId, outputDict);
                _logger.LogInformation(
                    "Extracted {Count} output params from step '{StepName}' for member {MemberId}",
                    outputDict.Count, stepExec.StepName, stepExec.BatchMemberId);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Failed to parse result JSON for output param extraction from step '{StepName}' for member {MemberId}",
                stepExec.StepName, stepExec.BatchMemberId);
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
            JobId = $"init-{nextStep.Id}",
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

    private async Task TriggerRollbackAsync(string rollbackName, string runbookName, int runbookVersion, int batchId, int? batchMemberId)
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

        // Load member data for per-member rollback template resolution
        Dictionary<string, string>? memberData = null;
        if (batchMemberId.HasValue)
        {
            var member = await _memberRepo.GetByIdAsync(batchMemberId.Value);
            if (member != null)
            {
                memberData = member.DataJson != null
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(member.DataJson)
                    : null;
            }
            else
            {
                _logger.LogWarning("Member {BatchMemberId} not found for rollback template resolution", batchMemberId.Value);
            }
        }

        await _rollbackExecutor.ExecuteRollbackAsync(rollbackName, definition, batch, memberData, batchMemberId);
    }
}
