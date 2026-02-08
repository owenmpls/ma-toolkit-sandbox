using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Exceptions;
using MaToolkit.Automation.Shared.Models.Db;
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
    private readonly ITemplateResolver _templateResolver;
    private readonly IPhaseEvaluator _phaseEvaluator;
    private readonly IPhaseProgressionService _progressionService;
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<PhaseDueHandler> _logger;

    public PhaseDueHandler(
        IBatchRepository batchRepo,
        IRunbookRepository runbookRepo,
        IPhaseExecutionRepository phaseRepo,
        IStepExecutionRepository stepRepo,
        IMemberRepository memberRepo,
        IWorkerDispatcher workerDispatcher,
        IRunbookParser runbookParser,
        ITemplateResolver templateResolver,
        IPhaseEvaluator phaseEvaluator,
        IPhaseProgressionService progressionService,
        IDbConnectionFactory db,
        ILogger<PhaseDueHandler> logger)
    {
        _batchRepo = batchRepo;
        _runbookRepo = runbookRepo;
        _phaseRepo = phaseRepo;
        _stepRepo = stepRepo;
        _memberRepo = memberRepo;
        _workerDispatcher = workerDispatcher;
        _runbookParser = runbookParser;
        _templateResolver = templateResolver;
        _phaseEvaluator = phaseEvaluator;
        _progressionService = progressionService;
        _db = db;
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

        if (phaseExecution.Status is PhaseStatus.Completed or PhaseStatus.Failed or PhaseStatus.Skipped or PhaseStatus.Superseded)
        {
            _logger.LogInformation(
                "Phase {PhaseExecutionId} already in terminal status '{Status}', skipping",
                message.PhaseExecutionId, phaseExecution.Status);
            return;
        }

        // Create step executions for this phase (idempotent — skips if steps already exist)
        await CreateStepExecutionsAsync(message, phaseExecution);

        // Get all step executions for this phase
        var allSteps = (await _stepRepo.GetByPhaseExecutionAsync(message.PhaseExecutionId))
            .OrderBy(s => s.StepIndex)
            .ThenBy(s => s.Id)
            .ToList();

        if (allSteps.Count == 0)
        {
            // No steps — check if all members are failed, otherwise mark complete
            var activeMembers = await _memberRepo.GetActiveByBatchAsync(message.BatchId);
            if (!activeMembers.Any())
            {
                _logger.LogWarning(
                    "No step executions and no active members for phase {PhaseExecutionId}, marking failed",
                    message.PhaseExecutionId);
                await _phaseRepo.SetFailedAsync(message.PhaseExecutionId);
            }
            else
            {
                _logger.LogInformation(
                    "No step executions for phase {PhaseExecutionId}, marking complete",
                    message.PhaseExecutionId);
                await _phaseRepo.SetCompletedAsync(message.PhaseExecutionId);
            }
            await _progressionService.CheckBatchCompletionAsync(message.BatchId);
            return;
        }

        // Per-member dispatch: find each member's next dispatchable step
        var memberGroups = allSteps.GroupBy(s => s.BatchMemberId).ToList();
        var jobsToDispatch = new List<(StepExecutionRecord Step, WorkerJobMessage Job)>();

        foreach (var memberSteps in memberGroups)
        {
            var steps = memberSteps.OrderBy(s => s.StepIndex).ToList();

            // Find this member's next pending step where all prior steps succeeded
            StepExecutionRecord? nextPending = null;
            var allPriorSucceeded = true;

            foreach (var step in steps)
            {
                if (step.Status == StepStatus.Pending && allPriorSucceeded)
                {
                    nextPending = step;
                    break;
                }
                else if (step.Status is StepStatus.Dispatched or StepStatus.Polling)
                {
                    // Member has an in-progress step — skip
                    break;
                }
                else if (step.Status == StepStatus.Succeeded)
                {
                    continue;
                }
                else
                {
                    // Failed/cancelled/poll_timeout — member already handled
                    allPriorSucceeded = false;
                    break;
                }
            }

            if (nextPending != null)
            {
                var job = new WorkerJobMessage
                {
                    JobId = $"step-{nextPending.Id}",
                    BatchId = message.BatchId,
                    WorkerId = nextPending.WorkerId!,
                    FunctionName = nextPending.FunctionName!,
                    Parameters = string.IsNullOrEmpty(nextPending.ParamsJson)
                        ? new Dictionary<string, string>()
                        : JsonSerializer.Deserialize<Dictionary<string, string>>(nextPending.ParamsJson) ?? new(),
                    CorrelationData = new JobCorrelationData
                    {
                        StepExecutionId = nextPending.Id,
                        IsInitStep = false,
                        RunbookName = message.RunbookName,
                        RunbookVersion = message.RunbookVersion
                    }
                };
                jobsToDispatch.Add((nextPending, job));
            }
        }

        if (jobsToDispatch.Count > 0)
        {
            _logger.LogInformation(
                "Dispatching {Count} steps for {MemberCount} members in phase {PhaseExecutionId}",
                jobsToDispatch.Count, jobsToDispatch.Count, message.PhaseExecutionId);

            await _workerDispatcher.DispatchJobsAsync(jobsToDispatch.Select(x => x.Job));

            foreach (var (step, job) in jobsToDispatch)
            {
                await _stepRepo.SetDispatchedAsync(step.Id, job.JobId);
            }

            // Set phase to dispatched if still pending
            await _phaseRepo.SetDispatchedAsync(message.PhaseExecutionId);
        }
        else
        {
            // No steps to dispatch — re-delivery or all members terminal
            await _progressionService.CheckPhaseCompletionAsync(message.PhaseExecutionId);
        }
    }

    private async Task CreateStepExecutionsAsync(PhaseDueMessage message, PhaseExecutionRecord phase)
    {
        // Check if steps already exist (idempotent — scheduler may have pre-created them during transition)
        var existingSteps = await _stepRepo.GetByPhaseExecutionAsync(message.PhaseExecutionId);
        if (existingSteps.Any())
            return;

        // Load runbook definition
        var runbook = await _runbookRepo.GetByNameAndVersionAsync(message.RunbookName, message.RunbookVersion);
        if (runbook is null)
        {
            _logger.LogError("Runbook {RunbookName} v{Version} not found for step creation",
                message.RunbookName, message.RunbookVersion);
            return;
        }

        var definition = _runbookParser.Parse(runbook.YamlContent);
        var phaseDefinition = definition.Phases.FirstOrDefault(p => p.Name == message.PhaseName);
        if (phaseDefinition is null)
        {
            _logger.LogError("Phase definition '{PhaseName}' not found in runbook {RunbookName} v{Version}",
                message.PhaseName, message.RunbookName, message.RunbookVersion);
            return;
        }

        // Load batch and members
        var batch = await _batchRepo.GetByIdAsync(message.BatchId);
        var members = (await _memberRepo.GetActiveByBatchAsync(message.BatchId)).ToList();
        if (members.Count == 0)
        {
            _logger.LogInformation("No active members for phase {PhaseExecutionId}, skipping step creation",
                message.PhaseExecutionId);
            return;
        }

        // Create step executions in a transaction
        using var conn = _db.CreateConnection();
        conn.Open();
        using var transaction = ((SqlConnection)conn).BeginTransaction();
        try
        {
            foreach (var member in members)
            {
                if (string.IsNullOrEmpty(member.DataJson))
                {
                    _logger.LogWarning("No data found for member {MemberKey}", member.MemberKey);
                    continue;
                }
                var dataRow = JsonSerializer.Deserialize<Dictionary<string, string>>(member.DataJson)!;

                // Merge worker output data for cross-phase resolution
                if (!string.IsNullOrEmpty(member.WorkerDataJson))
                {
                    var workerData = JsonSerializer.Deserialize<Dictionary<string, string>>(member.WorkerDataJson)!;
                    foreach (var (key, value) in workerData)
                        dataRow[key] = value;  // Worker wins on collision
                }

                try
                {
                    for (int i = 0; i < phaseDefinition.Steps.Count; i++)
                    {
                        var step = phaseDefinition.Steps[i];
                        var resolvedParams = _templateResolver.ResolveParams(
                            step.Params, dataRow, message.BatchId, batch?.BatchStartTime);
                        var resolvedFunction = _templateResolver.ResolveString(
                            step.Function, dataRow, message.BatchId, batch?.BatchStartTime);
                        var effectiveRetry = step.Retry ?? definition.Retry;

                        await _stepRepo.InsertAsync(new StepExecutionRecord
                        {
                            PhaseExecutionId = phase.Id,
                            BatchMemberId = member.Id,
                            StepName = step.Name,
                            StepIndex = i,
                            WorkerId = step.WorkerId,
                            FunctionName = resolvedFunction,
                            ParamsJson = JsonSerializer.Serialize(resolvedParams),
                            IsPollStep = step.Poll is not null,
                            PollIntervalSec = step.Poll is not null
                                ? _phaseEvaluator.ParseDurationSeconds(step.Poll.Interval) : null,
                            PollTimeoutSec = step.Poll is not null
                                ? _phaseEvaluator.ParseDurationSeconds(step.Poll.Timeout) : null,
                            OnFailure = step.OnFailure,
                            MaxRetries = effectiveRetry?.MaxRetries,
                            RetryIntervalSec = effectiveRetry is { MaxRetries: > 0 }
                                ? _phaseEvaluator.ParseDurationSeconds(effectiveRetry.Interval) : null
                        }, transaction);
                    }
                }
                catch (TemplateResolutionException ex)
                {
                    _logger.LogWarning(
                        "Skipping member {MemberKey}: unresolved template variables [{Variables}]",
                        member.MemberKey, string.Join(", ", ex.UnresolvedVariables));
                }
            }

            transaction.Commit();
            _logger.LogInformation(
                "Created step executions for phase {PhaseExecutionId} ({PhaseName}): {MemberCount} members × {StepCount} steps",
                message.PhaseExecutionId, message.PhaseName, members.Count, phaseDefinition.Steps.Count);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

}
