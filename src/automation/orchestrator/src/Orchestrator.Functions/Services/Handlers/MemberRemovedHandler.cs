using Microsoft.Extensions.Logging;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;

namespace Orchestrator.Functions.Services.Handlers;

public interface IMemberRemovedHandler
{
    Task HandleAsync(MemberRemovedMessage message);
}

public class MemberRemovedHandler : IMemberRemovedHandler
{
    private readonly IBatchRepository _batchRepo;
    private readonly IRunbookRepository _runbookRepo;
    private readonly IStepExecutionRepository _stepRepo;
    private readonly IMemberRepository _memberRepo;
    private readonly IWorkerDispatcher _workerDispatcher;
    private readonly IRunbookParser _runbookParser;
    private readonly ITemplateResolver _templateResolver;
    private readonly IPhaseEvaluator _phaseEvaluator;
    private readonly IDynamicTableReader _dynamicTableReader;
    private readonly ILogger<MemberRemovedHandler> _logger;

    public MemberRemovedHandler(
        IBatchRepository batchRepo,
        IRunbookRepository runbookRepo,
        IStepExecutionRepository stepRepo,
        IMemberRepository memberRepo,
        IWorkerDispatcher workerDispatcher,
        IRunbookParser runbookParser,
        ITemplateResolver templateResolver,
        IPhaseEvaluator phaseEvaluator,
        IDynamicTableReader dynamicTableReader,
        ILogger<MemberRemovedHandler> logger)
    {
        _batchRepo = batchRepo;
        _runbookRepo = runbookRepo;
        _stepRepo = stepRepo;
        _memberRepo = memberRepo;
        _workerDispatcher = workerDispatcher;
        _runbookParser = runbookParser;
        _templateResolver = templateResolver;
        _phaseEvaluator = phaseEvaluator;
        _dynamicTableReader = dynamicTableReader;
        _logger = logger;
    }

    public async Task HandleAsync(MemberRemovedMessage message)
    {
        _logger.LogInformation(
            "Processing member-removed for member {MemberKey} (id={BatchMemberId}) in batch {BatchId}",
            message.MemberKey, message.BatchMemberId, message.BatchId);

        // Cancel pending/dispatched steps for this member
        var pendingSteps = await _stepRepo.GetPendingByMemberAsync(message.BatchMemberId);
        var cancelledCount = 0;
        foreach (var step in pendingSteps)
        {
            await _stepRepo.SetCancelledAsync(step.Id);
            cancelledCount++;
        }

        if (cancelledCount > 0)
        {
            _logger.LogInformation(
                "Cancelled {Count} pending steps for member {MemberKey}",
                cancelledCount, message.MemberKey);
        }

        // Check if runbook has on_member_removed steps
        var runbook = await _runbookRepo.GetByNameAndVersionAsync(message.RunbookName, message.RunbookVersion);
        if (runbook == null)
        {
            _logger.LogWarning("Runbook {RunbookName} v{RunbookVersion} not found",
                message.RunbookName, message.RunbookVersion);
            return;
        }

        var definition = _runbookParser.Parse(runbook.YamlContent);

        if (definition.OnMemberRemoved.Count == 0)
        {
            _logger.LogInformation(
                "No on_member_removed steps defined for runbook {RunbookName}",
                message.RunbookName);
            return;
        }

        // Load member data from dynamic table (may still have last known data)
        var batch = await _batchRepo.GetByIdAsync(message.BatchId);
        if (batch == null)
        {
            _logger.LogError("Batch {BatchId} not found", message.BatchId);
            return;
        }

        var memberData = await _dynamicTableReader.GetMemberDataAsync(runbook.DataTableName, message.MemberKey);
        if (memberData == null)
        {
            _logger.LogWarning(
                "Member data not found for {MemberKey}, using limited template resolution",
                message.MemberKey);
        }

        // Dispatch on_member_removed steps
        _logger.LogInformation(
            "Dispatching {Count} on_member_removed steps for member {MemberKey}",
            definition.OnMemberRemoved.Count, message.MemberKey);

        for (var stepIndex = 0; stepIndex < definition.OnMemberRemoved.Count; stepIndex++)
        {
            var step = definition.OnMemberRemoved[stepIndex];

            Dictionary<string, string> resolvedParams;
            string resolvedFunction;

            if (memberData != null)
            {
                resolvedFunction = _templateResolver.ResolveString(
                    step.Function, memberData, batch.Id, batch.BatchStartTime);
                resolvedParams = _templateResolver.ResolveParams(
                    step.Params, memberData, batch.Id, batch.BatchStartTime);
            }
            else
            {
                // Limited resolution - only batch-level variables
                resolvedFunction = step.Function;
                resolvedParams = _templateResolver.ResolveInitParams(
                    step.Params, batch.Id, batch.BatchStartTime);
            }

            var job = new WorkerJobMessage
            {
                JobId = $"removed-{message.BatchMemberId}-{stepIndex}",
                BatchId = message.BatchId,
                WorkerId = step.WorkerId,
                FunctionName = resolvedFunction,
                Parameters = resolvedParams,
                CorrelationData = new JobCorrelationData
                {
                    IsInitStep = false,
                    RunbookName = message.RunbookName,
                    RunbookVersion = message.RunbookVersion
                }
            };

            await _workerDispatcher.DispatchJobAsync(job);

            _logger.LogInformation(
                "Dispatched on_member_removed step '{StepName}' (job {JobId}) for member {MemberKey}",
                step.Name, job.JobId, message.MemberKey);
        }
    }
}
