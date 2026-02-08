using System.Text.Json;
using Microsoft.Extensions.Logging;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Services.Repositories;

namespace Orchestrator.Functions.Services.Handlers;

public interface IRetryCheckHandler
{
    Task HandleAsync(RetryCheckMessage message);
}

public class RetryCheckHandler : IRetryCheckHandler
{
    private readonly IStepExecutionRepository _stepRepo;
    private readonly IInitExecutionRepository _initRepo;
    private readonly IWorkerDispatcher _workerDispatcher;
    private readonly ILogger<RetryCheckHandler> _logger;

    public RetryCheckHandler(
        IStepExecutionRepository stepRepo,
        IInitExecutionRepository initRepo,
        IWorkerDispatcher workerDispatcher,
        ILogger<RetryCheckHandler> logger)
    {
        _stepRepo = stepRepo;
        _initRepo = initRepo;
        _workerDispatcher = workerDispatcher;
        _logger = logger;
    }

    public async Task HandleAsync(RetryCheckMessage message)
    {
        if (message.IsInitStep)
        {
            await HandleInitRetryAsync(message);
        }
        else
        {
            await HandleStepRetryAsync(message);
        }
    }

    private async Task HandleStepRetryAsync(RetryCheckMessage message)
    {
        var step = await _stepRepo.GetByIdAsync(message.StepExecutionId);
        if (step == null)
        {
            _logger.LogWarning("Step execution {StepExecutionId} not found for retry", message.StepExecutionId);
            return;
        }

        if (step.Status != StepStatus.Pending)
        {
            _logger.LogInformation(
                "Step execution {StepExecutionId} is not pending (status={Status}), skipping retry dispatch",
                step.Id, step.Status);
            return;
        }

        var job = new WorkerJobMessage
        {
            JobId = $"step-{step.Id}-retry-{step.RetryCount}",
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
        await _stepRepo.SetDispatchedAsync(step.Id, job.JobId);

        _logger.LogInformation(
            "Dispatched retry #{RetryCount} for step execution {StepExecutionId} (job {JobId})",
            step.RetryCount, step.Id, job.JobId);
    }

    private async Task HandleInitRetryAsync(RetryCheckMessage message)
    {
        var initExec = await _initRepo.GetByIdAsync(message.StepExecutionId);
        if (initExec == null)
        {
            _logger.LogWarning("Init execution {InitExecutionId} not found for retry", message.StepExecutionId);
            return;
        }

        if (initExec.Status != StepStatus.Pending)
        {
            _logger.LogInformation(
                "Init execution {InitExecutionId} is not pending (status={Status}), skipping retry dispatch",
                initExec.Id, initExec.Status);
            return;
        }

        var job = new WorkerJobMessage
        {
            JobId = $"init-{initExec.Id}-retry-{initExec.RetryCount}",
            BatchId = message.BatchId,
            WorkerId = initExec.WorkerId!,
            FunctionName = initExec.FunctionName!,
            Parameters = string.IsNullOrEmpty(initExec.ParamsJson)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(initExec.ParamsJson) ?? new(),
            CorrelationData = new JobCorrelationData
            {
                InitExecutionId = initExec.Id,
                IsInitStep = true,
                RunbookName = message.RunbookName,
                RunbookVersion = message.RunbookVersion
            }
        };

        await _workerDispatcher.DispatchJobAsync(job);
        await _initRepo.SetDispatchedAsync(initExec.Id, job.JobId);

        _logger.LogInformation(
            "Dispatched retry #{RetryCount} for init execution {InitExecutionId} (job {JobId})",
            initExec.RetryCount, initExec.Id, job.JobId);
    }
}
