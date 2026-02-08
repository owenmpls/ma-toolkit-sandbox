using System.Text.Json;
using Microsoft.Extensions.Logging;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;

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
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<BatchInitHandler> _logger;

    public BatchInitHandler(
        IBatchRepository batchRepo,
        IRunbookRepository runbookRepo,
        IInitExecutionRepository initRepo,
        IWorkerDispatcher workerDispatcher,
        IRunbookParser runbookParser,
        ITemplateResolver templateResolver,
        IPhaseEvaluator phaseEvaluator,
        IDbConnectionFactory db,
        ILogger<BatchInitHandler> logger)
    {
        _batchRepo = batchRepo;
        _runbookRepo = runbookRepo;
        _initRepo = initRepo;
        _workerDispatcher = workerDispatcher;
        _runbookParser = runbookParser;
        _templateResolver = templateResolver;
        _phaseEvaluator = phaseEvaluator;
        _db = db;
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

        // Create init executions on demand (idempotent — skips if version already exists)
        await CreateInitExecutionsAsync(message, definition);

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

    private async Task CreateInitExecutionsAsync(BatchInitMessage message, RunbookDefinition definition)
    {
        // Version-aware idempotency check: a batch can have init_executions from multiple
        // runbook versions (via version transitions), so check by version not just batch
        var existingInits = await _initRepo.GetByBatchAsync(message.BatchId);
        if (existingInits.Any(i => i.RunbookVersion == message.RunbookVersion))
            return;

        if (definition.Init.Count == 0)
            return;

        using var conn = _db.CreateConnection();
        conn.Open();
        using var transaction = conn.BeginTransaction();
        try
        {
            for (int i = 0; i < definition.Init.Count; i++)
            {
                var initStep = definition.Init[i];
                var effectiveRetry = initStep.Retry ?? definition.Retry;
                await _initRepo.InsertAsync(new InitExecutionRecord
                {
                    BatchId = message.BatchId,
                    StepName = initStep.Name,
                    StepIndex = i,
                    RunbookVersion = message.RunbookVersion,
                    WorkerId = initStep.WorkerId,
                    FunctionName = initStep.Function,
                    ParamsJson = initStep.Params.Count > 0
                        ? JsonSerializer.Serialize(
                            _templateResolver.ResolveInitParams(initStep.Params, message.BatchId, message.BatchStartTime))
                        : null,
                    IsPollStep = initStep.Poll is not null,
                    PollIntervalSec = initStep.Poll is not null
                        ? _phaseEvaluator.ParseDurationSeconds(initStep.Poll.Interval) : null,
                    PollTimeoutSec = initStep.Poll is not null
                        ? _phaseEvaluator.ParseDurationSeconds(initStep.Poll.Timeout) : null,
                    OnFailure = initStep.OnFailure,
                    MaxRetries = effectiveRetry?.MaxRetries,
                    RetryIntervalSec = effectiveRetry is { MaxRetries: > 0 }
                        ? _phaseEvaluator.ParseDurationSeconds(effectiveRetry.Interval) : null
                }, transaction);
            }

            transaction.Commit();
            _logger.LogInformation(
                "Created {Count} init executions for batch {BatchId} (v{Version})",
                definition.Init.Count, message.BatchId, message.RunbookVersion);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
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
