using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orchestrator.Functions.Models.Messages;
using Orchestrator.Functions.Services.Repositories;

namespace Orchestrator.Functions.Services.Handlers;

public interface IPollCheckHandler
{
    Task HandleAsync(PollCheckMessage message);
}

public class PollCheckHandler : IPollCheckHandler
{
    private readonly IStepExecutionRepository _stepRepo;
    private readonly IInitExecutionRepository _initRepo;
    private readonly IBatchRepository _batchRepo;
    private readonly IWorkerDispatcher _workerDispatcher;
    private readonly IRollbackExecutor _rollbackExecutor;
    private readonly IRunbookRepository _runbookRepo;
    private readonly IRunbookParser _runbookParser;
    private readonly ILogger<PollCheckHandler> _logger;

    public PollCheckHandler(
        IStepExecutionRepository stepRepo,
        IInitExecutionRepository initRepo,
        IBatchRepository batchRepo,
        IWorkerDispatcher workerDispatcher,
        IRollbackExecutor rollbackExecutor,
        IRunbookRepository runbookRepo,
        IRunbookParser runbookParser,
        ILogger<PollCheckHandler> logger)
    {
        _stepRepo = stepRepo;
        _initRepo = initRepo;
        _batchRepo = batchRepo;
        _workerDispatcher = workerDispatcher;
        _rollbackExecutor = rollbackExecutor;
        _runbookRepo = runbookRepo;
        _runbookParser = runbookParser;
        _logger = logger;
    }

    public async Task HandleAsync(PollCheckMessage message)
    {
        _logger.LogDebug(
            "Processing poll-check for step {StepExecutionId} (poll #{PollCount}), isInit={IsInitStep}",
            message.StepExecutionId, message.PollCount, message.IsInitStep);

        if (message.IsInitStep)
        {
            await HandleInitPollCheckAsync(message);
        }
        else
        {
            await HandleStepPollCheckAsync(message);
        }
    }

    private async Task HandleInitPollCheckAsync(PollCheckMessage message)
    {
        var step = await _initRepo.GetByIdAsync(message.StepExecutionId);
        if (step == null)
        {
            _logger.LogWarning("Init execution {StepExecutionId} not found", message.StepExecutionId);
            return;
        }

        if (step.Status != "polling")
        {
            _logger.LogDebug(
                "Init execution {StepExecutionId} is not polling (status={Status}), skipping",
                message.StepExecutionId, step.Status);
            return;
        }

        // Check for timeout
        if (step.PollStartedAt.HasValue && step.PollTimeoutSec.HasValue)
        {
            var elapsed = DateTime.UtcNow - step.PollStartedAt.Value;
            if (elapsed.TotalSeconds > step.PollTimeoutSec.Value)
            {
                _logger.LogWarning(
                    "Init execution {StepExecutionId} poll timed out after {Elapsed}s (timeout={Timeout}s)",
                    step.Id, elapsed.TotalSeconds, step.PollTimeoutSec.Value);

                await _initRepo.SetPollTimeoutAsync(step.Id);
                await _batchRepo.SetFailedAsync(step.BatchId);

                // Trigger rollback if configured
                if (!string.IsNullOrEmpty(step.OnFailure))
                {
                    await TriggerRollbackAsync(step.OnFailure, message.RunbookName, message.RunbookVersion, step.BatchId);
                }
                return;
            }
        }

        // Re-dispatch the same job
        var job = new WorkerJobMessage
        {
            JobId = step.JobId ?? Guid.NewGuid().ToString(),
            BatchId = step.BatchId,
            WorkerId = step.WorkerId!,
            FunctionName = step.FunctionName!,
            Parameters = string.IsNullOrEmpty(step.ParamsJson)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(step.ParamsJson) ?? new(),
            CorrelationData = new JobCorrelationData
            {
                InitExecutionId = step.Id,
                IsInitStep = true,
                RunbookName = message.RunbookName,
                RunbookVersion = message.RunbookVersion
            }
        };

        await _workerDispatcher.DispatchJobAsync(job);
        await _initRepo.UpdatePollStateAsync(step.Id);

        _logger.LogDebug(
            "Re-dispatched poll for init execution {StepExecutionId} (poll #{PollCount})",
            step.Id, step.PollCount + 1);
    }

    private async Task HandleStepPollCheckAsync(PollCheckMessage message)
    {
        var step = await _stepRepo.GetByIdAsync(message.StepExecutionId);
        if (step == null)
        {
            _logger.LogWarning("Step execution {StepExecutionId} not found", message.StepExecutionId);
            return;
        }

        if (step.Status != "polling")
        {
            _logger.LogDebug(
                "Step execution {StepExecutionId} is not polling (status={Status}), skipping",
                message.StepExecutionId, step.Status);
            return;
        }

        // Check for timeout
        if (step.PollStartedAt.HasValue && step.PollTimeoutSec.HasValue)
        {
            var elapsed = DateTime.UtcNow - step.PollStartedAt.Value;
            if (elapsed.TotalSeconds > step.PollTimeoutSec.Value)
            {
                _logger.LogWarning(
                    "Step execution {StepExecutionId} poll timed out after {Elapsed}s (timeout={Timeout}s)",
                    step.Id, elapsed.TotalSeconds, step.PollTimeoutSec.Value);

                await _stepRepo.SetPollTimeoutAsync(step.Id);

                // Trigger rollback if configured
                if (!string.IsNullOrEmpty(step.OnFailure))
                {
                    await TriggerRollbackAsync(step.OnFailure, message.RunbookName, message.RunbookVersion, message.BatchId);
                }
                return;
            }
        }

        // Re-dispatch the same job
        var job = new WorkerJobMessage
        {
            JobId = step.JobId ?? Guid.NewGuid().ToString(),
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

        await _workerDispatcher.DispatchJobAsync(job);
        await _stepRepo.UpdatePollStateAsync(step.Id);

        _logger.LogDebug(
            "Re-dispatched poll for step execution {StepExecutionId} (poll #{PollCount})",
            step.Id, step.PollCount + 1);
    }

    private async Task TriggerRollbackAsync(string rollbackName, string runbookName, int runbookVersion, int batchId)
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

        await _rollbackExecutor.ExecuteRollbackAsync(rollbackName, definition, batch, null, null);
    }
}
