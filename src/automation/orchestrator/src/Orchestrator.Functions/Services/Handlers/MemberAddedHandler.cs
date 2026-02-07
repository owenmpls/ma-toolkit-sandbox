using System.Text.Json;
using Microsoft.Extensions.Logging;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;

namespace Orchestrator.Functions.Services.Handlers;

public interface IMemberAddedHandler
{
    Task HandleAsync(MemberAddedMessage message);
}

public class MemberAddedHandler : IMemberAddedHandler
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
    private readonly IDynamicTableReader _dynamicTableReader;
    private readonly ILogger<MemberAddedHandler> _logger;

    public MemberAddedHandler(
        IBatchRepository batchRepo,
        IRunbookRepository runbookRepo,
        IPhaseExecutionRepository phaseRepo,
        IStepExecutionRepository stepRepo,
        IMemberRepository memberRepo,
        IWorkerDispatcher workerDispatcher,
        IRunbookParser runbookParser,
        ITemplateResolver templateResolver,
        IPhaseEvaluator phaseEvaluator,
        IDynamicTableReader dynamicTableReader,
        ILogger<MemberAddedHandler> logger)
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
        _dynamicTableReader = dynamicTableReader;
        _logger = logger;
    }

    public async Task HandleAsync(MemberAddedMessage message)
    {
        _logger.LogInformation(
            "Processing member-added for member {MemberKey} (id={BatchMemberId}) in batch {BatchId}",
            message.MemberKey, message.BatchMemberId, message.BatchId);

        var batch = await _batchRepo.GetByIdAsync(message.BatchId);
        if (batch == null)
        {
            _logger.LogError("Batch {BatchId} not found", message.BatchId);
            return;
        }

        if (batch.Status != BatchStatus.Active)
        {
            _logger.LogWarning(
                "Batch {BatchId} is not active (status={Status}), skipping member-added",
                message.BatchId, batch.Status);
            return;
        }

        var runbook = await _runbookRepo.GetByNameAndVersionAsync(message.RunbookName, message.RunbookVersion);
        if (runbook == null)
        {
            _logger.LogError("Runbook {RunbookName} v{RunbookVersion} not found",
                message.RunbookName, message.RunbookVersion);
            return;
        }

        var definition = _runbookParser.Parse(runbook.YamlContent);

        // Load member data from dynamic table
        var memberData = await _dynamicTableReader.GetMemberDataAsync(runbook.DataTableName, message.MemberKey);
        if (memberData == null)
        {
            _logger.LogError("Member data not found for {MemberKey} in table {TableName}",
                message.MemberKey, runbook.DataTableName);
            return;
        }

        // Get overdue phases (dispatched or completed)
        var overduePhases = (await _phaseRepo.GetDispatchedByBatchAsync(message.BatchId))
            .OrderBy(p => p.OffsetMinutes)
            .ToList();

        if (overduePhases.Count == 0)
        {
            _logger.LogInformation(
                "No overdue phases for batch {BatchId}, member {MemberKey} will be processed with regular phases",
                message.BatchId, message.MemberKey);
            return;
        }

        _logger.LogInformation(
            "Creating catch-up step executions for {PhaseCount} overdue phases for member {MemberKey}",
            overduePhases.Count, message.MemberKey);

        // For each overdue phase, create step executions for this new member
        foreach (var phaseExec in overduePhases)
        {
            var phaseDefinition = definition.Phases.FirstOrDefault(p => p.Name == phaseExec.PhaseName);
            if (phaseDefinition == null)
            {
                _logger.LogWarning("Phase definition '{PhaseName}' not found in runbook", phaseExec.PhaseName);
                continue;
            }

            // Create step executions for each step in the phase
            var jobs = new List<WorkerJobMessage>();
            var stepRecords = new List<StepExecutionRecord>();

            for (var stepIndex = 0; stepIndex < phaseDefinition.Steps.Count; stepIndex++)
            {
                var step = phaseDefinition.Steps[stepIndex];

                // Resolve function name and parameters using member data
                var resolvedFunction = _templateResolver.ResolveString(
                    step.Function, memberData, batch.Id, batch.BatchStartTime);
                var resolvedParams = _templateResolver.ResolveParams(
                    step.Params, memberData, batch.Id, batch.BatchStartTime);

                var stepRecord = new StepExecutionRecord
                {
                    PhaseExecutionId = phaseExec.Id,
                    BatchMemberId = message.BatchMemberId,
                    StepName = step.Name,
                    StepIndex = stepIndex,
                    WorkerId = step.WorkerId,
                    FunctionName = resolvedFunction,
                    ParamsJson = JsonSerializer.Serialize(resolvedParams),
                    IsPollStep = step.Poll != null,
                    PollIntervalSec = step.Poll != null ? _phaseEvaluator.ParseDurationSeconds(step.Poll.Interval) : null,
                    PollTimeoutSec = step.Poll != null ? _phaseEvaluator.ParseDurationSeconds(step.Poll.Timeout) : null,
                    OnFailure = step.OnFailure
                };

                var stepId = await _stepRepo.InsertAsync(stepRecord);
                stepRecord.Id = stepId;
                stepRecords.Add(stepRecord);
            }

            // Dispatch first step_index for this phase (sequential within phase)
            var firstSteps = stepRecords.Where(s => s.StepIndex == 0).ToList();
            foreach (var step in firstSteps)
            {
                var job = new WorkerJobMessage
                {
                    JobId = $"step-{step.Id}",
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
                    "Dispatched catch-up step '{StepName}' for member {MemberKey} in phase {PhaseName}",
                    step.StepName, message.MemberKey, phaseExec.PhaseName);
            }
        }
    }
}
