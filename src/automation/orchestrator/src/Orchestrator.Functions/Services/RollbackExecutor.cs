using System.Data;
using Microsoft.Extensions.Logging;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;

namespace Orchestrator.Functions.Services;

public interface IRollbackExecutor
{
    /// <summary>
    /// Execute a rollback sequence for a failed step.
    /// </summary>
    Task ExecuteRollbackAsync(
        string rollbackName,
        RunbookDefinition runbook,
        BatchRecord batch,
        DataRow? memberData,
        int? batchMemberId);
}

public class RollbackExecutor : IRollbackExecutor
{
    private readonly IWorkerDispatcher _workerDispatcher;
    private readonly IStepExecutionRepository _stepExecutionRepo;
    private readonly ITemplateResolver _templateResolver;
    private readonly IPhaseEvaluator _phaseEvaluator;
    private readonly ILogger<RollbackExecutor> _logger;

    public RollbackExecutor(
        IWorkerDispatcher workerDispatcher,
        IStepExecutionRepository stepExecutionRepo,
        ITemplateResolver templateResolver,
        IPhaseEvaluator phaseEvaluator,
        ILogger<RollbackExecutor> logger)
    {
        _workerDispatcher = workerDispatcher;
        _stepExecutionRepo = stepExecutionRepo;
        _templateResolver = templateResolver;
        _phaseEvaluator = phaseEvaluator;
        _logger = logger;
    }

    public async Task ExecuteRollbackAsync(
        string rollbackName,
        RunbookDefinition runbook,
        BatchRecord batch,
        DataRow? memberData,
        int? batchMemberId)
    {
        if (!runbook.Rollbacks.TryGetValue(rollbackName, out var rollbackSteps))
        {
            _logger.LogWarning(
                "Rollback sequence '{RollbackName}' not found in runbook {RunbookName}",
                rollbackName, runbook.Name);
            return;
        }

        _logger.LogInformation(
            "Executing rollback sequence '{RollbackName}' for batch {BatchId}",
            rollbackName, batch.Id);

        // Execute rollback steps sequentially
        for (var stepIndex = 0; stepIndex < rollbackSteps.Count; stepIndex++)
        {
            var step = rollbackSteps[stepIndex];
            try
            {
                // Resolve parameters
                Dictionary<string, string> resolvedParams;
                if (memberData != null)
                {
                    resolvedParams = _templateResolver.ResolveParams(
                        step.Params, memberData, batch.Id, batch.BatchStartTime);
                }
                else
                {
                    resolvedParams = _templateResolver.ResolveInitParams(
                        step.Params, batch.Id, batch.BatchStartTime);
                }

                var job = new WorkerJobMessage
                {
                    JobId = $"rollback-{batch.Id}-{rollbackName}-{stepIndex}",
                    BatchId = batch.Id,
                    WorkerId = step.WorkerId,
                    FunctionName = step.Function,
                    Parameters = resolvedParams,
                    CorrelationData = new JobCorrelationData
                    {
                        IsInitStep = memberData == null,
                        RunbookName = runbook.Name,
                        RunbookVersion = 0 // Rollback steps don't need version tracking
                    }
                };

                await _workerDispatcher.DispatchJobAsync(job);

                _logger.LogInformation(
                    "Dispatched rollback step '{StepName}' (job {JobId}) for batch {BatchId}",
                    step.Name, job.JobId, batch.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to dispatch rollback step '{StepName}' for batch {BatchId}",
                    step.Name, batch.Id);
                // Continue with other rollback steps even if one fails
            }
        }
    }
}
