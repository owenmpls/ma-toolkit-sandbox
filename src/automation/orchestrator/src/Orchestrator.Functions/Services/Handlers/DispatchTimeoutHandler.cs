using Microsoft.Extensions.Logging;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Services.Repositories;

namespace Orchestrator.Functions.Services.Handlers;

public interface IDispatchTimeoutHandler
{
    Task HandleAsync(DispatchTimeoutMessage message);
}

public class DispatchTimeoutHandler : IDispatchTimeoutHandler
{
    private readonly IStepExecutionRepository _stepRepo;
    private readonly IInitExecutionRepository _initRepo;
    private readonly IBatchRepository _batchRepo;
    private readonly IPhaseProgressionService _progressionService;
    private readonly ILogger<DispatchTimeoutHandler> _logger;

    public DispatchTimeoutHandler(
        IStepExecutionRepository stepRepo,
        IInitExecutionRepository initRepo,
        IBatchRepository batchRepo,
        IPhaseProgressionService progressionService,
        ILogger<DispatchTimeoutHandler> logger)
    {
        _stepRepo = stepRepo;
        _initRepo = initRepo;
        _batchRepo = batchRepo;
        _progressionService = progressionService;
        _logger = logger;
    }

    public async Task HandleAsync(DispatchTimeoutMessage message)
    {
        _logger.LogWarning(
            "Processing dispatch-timeout for step {StepExecutionId} (step {StepName}), isInit={IsInitStep}",
            message.StepExecutionId, message.StepName, message.IsInitStep);

        if (message.IsInitStep)
        {
            await HandleInitTimeoutAsync(message);
        }
        else
        {
            await HandleStepTimeoutAsync(message);
        }
    }

    private async Task HandleInitTimeoutAsync(DispatchTimeoutMessage message)
    {
        var step = await _initRepo.GetByIdAsync(message.StepExecutionId);
        if (step == null)
        {
            _logger.LogWarning("Init execution {StepExecutionId} not found for dispatch timeout", message.StepExecutionId);
            return;
        }

        if (step.Status != StepStatus.Dispatched)
        {
            _logger.LogInformation(
                "Init execution {StepExecutionId} is no longer dispatched (status={Status}), skipping timeout",
                message.StepExecutionId, step.Status);
            return;
        }

        _logger.LogWarning(
            "Init execution {StepExecutionId} timed out (dispatched at {DispatchedAt}), marking failed",
            step.Id, message.DispatchedAt);

        await _initRepo.SetFailedAsync(step.Id, "Dispatch timeout — no result received from worker");
        await _batchRepo.SetFailedAsync(step.BatchId);
    }

    private async Task HandleStepTimeoutAsync(DispatchTimeoutMessage message)
    {
        var step = await _stepRepo.GetByIdAsync(message.StepExecutionId);
        if (step == null)
        {
            _logger.LogWarning("Step execution {StepExecutionId} not found for dispatch timeout", message.StepExecutionId);
            return;
        }

        if (step.Status != StepStatus.Dispatched)
        {
            _logger.LogInformation(
                "Step execution {StepExecutionId} is no longer dispatched (status={Status}), skipping timeout",
                message.StepExecutionId, step.Status);
            return;
        }

        _logger.LogWarning(
            "Step execution {StepExecutionId} timed out (dispatched at {DispatchedAt}), marking failed",
            step.Id, message.DispatchedAt);

        await _stepRepo.SetFailedAsync(step.Id, "Dispatch timeout — no result received from worker");
        await _progressionService.HandleMemberFailureAsync(step.PhaseExecutionId, step.BatchMemberId);
    }
}
