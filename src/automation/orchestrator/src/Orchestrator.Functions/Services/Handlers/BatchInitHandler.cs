using System.Text.Json;
using Microsoft.Extensions.Logging;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using Orchestrator.Functions.Services.Repositories;

namespace Orchestrator.Functions.Services.Handlers;

public interface IBatchInitHandler
{
    Task HandleAsync(BatchInitMessage message);
}

public class BatchInitHandler : IBatchInitHandler
{
    private readonly IBatchRepository _batchRepo;
    private readonly IRunbookRepository _runbookRepo;
    private readonly IInitExecutionRepository _initRepo;
    private readonly IWorkerDispatcher _workerDispatcher;
    private readonly IRunbookParser _runbookParser;
    private readonly ITemplateResolver _templateResolver;
    private readonly IPhaseEvaluator _phaseEvaluator;
    private readonly ILogger<BatchInitHandler> _logger;

    public BatchInitHandler(
        IBatchRepository batchRepo,
        IRunbookRepository runbookRepo,
        IInitExecutionRepository initRepo,
        IWorkerDispatcher workerDispatcher,
        IRunbookParser runbookParser,
        ITemplateResolver templateResolver,
        IPhaseEvaluator phaseEvaluator,
        ILogger<BatchInitHandler> logger)
    {
        _batchRepo = batchRepo;
        _runbookRepo = runbookRepo;
        _initRepo = initRepo;
        _workerDispatcher = workerDispatcher;
        _runbookParser = runbookParser;
        _templateResolver = templateResolver;
        _phaseEvaluator = phaseEvaluator;
        _logger = logger;
    }

    public async Task HandleAsync(BatchInitMessage message)
    {
        _logger.LogInformation(
            "Processing batch-init for batch {BatchId} ({RunbookName} v{RunbookVersion})",
            message.BatchId, message.RunbookName, message.RunbookVersion);

        var batch = await _batchRepo.GetByIdAsync(message.BatchId);
        if (batch == null)
        {
            _logger.LogError("Batch {BatchId} not found", message.BatchId);
            return;
        }

        var runbook = await _runbookRepo.GetByNameAndVersionAsync(message.RunbookName, message.RunbookVersion);
        if (runbook == null)
        {
            _logger.LogError("Runbook {RunbookName} v{RunbookVersion} not found",
                message.RunbookName, message.RunbookVersion);
            await _batchRepo.SetFailedAsync(message.BatchId);
            return;
        }

        var definition = _runbookParser.Parse(runbook.YamlContent);

        // Get pending init steps ordered by step_index
        var initSteps = (await _initRepo.GetPendingByBatchAsync(message.BatchId))
            .OrderBy(s => s.StepIndex)
            .ToList();

        if (initSteps.Count == 0)
        {
            _logger.LogInformation("No pending init steps for batch {BatchId}, setting to active", message.BatchId);
            await _batchRepo.SetActiveAsync(message.BatchId);
            return;
        }

        // Dispatch init steps sequentially (one at a time)
        // The ResultProcessor will advance to the next step on success
        var firstStep = initSteps.First();
        await DispatchInitStepAsync(firstStep, batch, definition);
    }

    private async Task DispatchInitStepAsync(InitExecutionRecord step, BatchRecord batch, RunbookDefinition definition)
    {
        var job = new WorkerJobMessage
        {
            JobId = $"init-{step.Id}",
            BatchId = batch.Id,
            WorkerId = step.WorkerId!,
            FunctionName = step.FunctionName!,
            Parameters = string.IsNullOrEmpty(step.ParamsJson)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(step.ParamsJson) ?? new(),
            CorrelationData = new JobCorrelationData
            {
                InitExecutionId = step.Id,
                IsInitStep = true,
                RunbookName = definition.Name,
                RunbookVersion = step.RunbookVersion
            }
        };

        await _workerDispatcher.DispatchJobAsync(job);
        await _initRepo.SetDispatchedAsync(step.Id, job.JobId);

        _logger.LogInformation(
            "Dispatched init step '{StepName}' (job {JobId}) for batch {BatchId}",
            step.StepName, job.JobId, batch.Id);
    }
}
