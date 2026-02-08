using System.Text.Json;
using Microsoft.Extensions.Logging;
using MaToolkit.Automation.Shared.Constants;
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
    private readonly IDynamicTableReader _dynamicTableReader;
    private readonly IMemberRepository _memberRepo;
    private readonly IPhaseProgressionService _progressionService;
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
        IMemberRepository memberRepo,
        IPhaseProgressionService progressionService,
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
        _memberRepo = memberRepo;
        _progressionService = progressionService;
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
        System.Data.DataRow? memberData = null;
        if (batchMemberId.HasValue)
        {
            var member = await _memberRepo.GetByIdAsync(batchMemberId.Value);
            if (member != null)
            {
                memberData = await _dynamicTableReader.GetMemberDataAsync(runbook.DataTableName, member.MemberKey);
            }
            else
            {
                _logger.LogWarning("Member {BatchMemberId} not found for rollback template resolution", batchMemberId.Value);
            }
        }

        await _rollbackExecutor.ExecuteRollbackAsync(rollbackName, definition, batch, memberData, batchMemberId);
    }
}
