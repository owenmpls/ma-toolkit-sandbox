using System.Data;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MaToolkit.Automation.Shared.Services.Repositories;

namespace AdminApi.Functions.Services;

public interface IManualBatchService
{
    Task<ManualBatchResult> CreateBatchAsync(
        RunbookRecord runbook,
        RunbookDefinition definition,
        DataTable memberData,
        string? createdBy);

    Task<AdvanceResult> AdvanceBatchAsync(BatchRecord batch, RunbookRecord runbook, RunbookDefinition definition);
}

public class ManualBatchResult
{
    public bool Success { get; set; }
    public int BatchId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int MemberCount { get; set; }
    public List<string> AvailablePhases { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

public class AdvanceResult
{
    public bool Success { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? PhaseName { get; set; }
    public int MemberCount { get; set; }
    public int StepCount { get; set; }
    public string? NextPhase { get; set; }
    public string? ErrorMessage { get; set; }
}

public class ManualBatchService : IManualBatchService
{
    private readonly IBatchRepository _batchRepo;
    private readonly IMemberRepository _memberRepo;
    private readonly IPhaseExecutionRepository _phaseRepo;
    private readonly IInitExecutionRepository _initRepo;
    private readonly IDynamicTableManager _dynamicTableManager;
    private readonly IPhaseEvaluator _phaseEvaluator;
    private readonly IDbConnectionFactory _db;
    private readonly ServiceBusClient? _serviceBusClient;
    private readonly ILogger<ManualBatchService> _logger;

    public ManualBatchService(
        IBatchRepository batchRepo,
        IMemberRepository memberRepo,
        IPhaseExecutionRepository phaseRepo,
        IInitExecutionRepository initRepo,
        IDynamicTableManager dynamicTableManager,
        IPhaseEvaluator phaseEvaluator,
        IDbConnectionFactory db,
        ILogger<ManualBatchService> logger,
        ServiceBusClient? serviceBusClient = null)
    {
        _batchRepo = batchRepo;
        _memberRepo = memberRepo;
        _phaseRepo = phaseRepo;
        _initRepo = initRepo;
        _dynamicTableManager = dynamicTableManager;
        _phaseEvaluator = phaseEvaluator;
        _db = db;
        _serviceBusClient = serviceBusClient;
        _logger = logger;
    }

    public async Task<ManualBatchResult> CreateBatchAsync(
        RunbookRecord runbook,
        RunbookDefinition definition,
        DataTable memberData,
        string? createdBy)
    {
        var result = new ManualBatchResult();

        try
        {
            _logger.LogInformation(
                "Creating manual batch for runbook {RunbookName} with {MemberCount} members",
                runbook.Name, memberData.Rows.Count);

            using var conn = _db.CreateConnection();
            conn.Open();
            using var transaction = ((SqlConnection)conn).BeginTransaction();

            try
            {
                var hasInitSteps = definition.Init.Count > 0;

                // Create batch record
                var batch = new BatchRecord
                {
                    RunbookId = runbook.Id,
                    BatchStartTime = null, // Manual batches don't use batch_start_time for scheduling
                    Status = hasInitSteps ? BatchStatus.Detected : BatchStatus.Active,
                    IsManual = true,
                    CreatedBy = createdBy,
                    CurrentPhase = null
                };
                var batchId = await _batchRepo.InsertAsync(batch, transaction);

                // Ensure dynamic table exists
                var queryColumns = memberData.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
                await _dynamicTableManager.EnsureTableAsync(
                    runbook.DataTableName,
                    queryColumns,
                    definition.DataSource.MultiValuedColumns);

                // Upsert member data into dynamic table
                await _dynamicTableManager.UpsertDataAsync(
                    runbook.DataTableName,
                    definition.DataSource.PrimaryKey,
                    definition.DataSource.BatchTimeColumn,
                    memberData,
                    definition.DataSource.MultiValuedColumns);

                // Insert batch members
                var primaryKey = definition.DataSource.PrimaryKey;
                foreach (DataRow row in memberData.Rows)
                {
                    var memberKey = row[primaryKey]?.ToString() ?? string.Empty;
                    await _memberRepo.InsertAsync(new BatchMemberRecord
                    {
                        BatchId = batchId,
                        MemberKey = memberKey
                    }, transaction);
                }

                // Create phase executions (without due_at for manual batches)
                foreach (var phase in definition.Phases)
                {
                    var offsetMinutes = _phaseEvaluator.ParseOffsetMinutes(phase.Offset);
                    await _phaseRepo.InsertAsync(new PhaseExecutionRecord
                    {
                        BatchId = batchId,
                        PhaseName = phase.Name,
                        OffsetMinutes = offsetMinutes,
                        DueAt = null, // Manual batches don't use time-based scheduling
                        RunbookVersion = runbook.Version,
                        Status = PhaseStatus.Pending
                    }, transaction);
                }

                // Create init executions if runbook has init steps
                if (hasInitSteps)
                {
                    for (int i = 0; i < definition.Init.Count; i++)
                    {
                        var initStep = definition.Init[i];
                        await _initRepo.InsertAsync(new InitExecutionRecord
                        {
                            BatchId = batchId,
                            StepName = initStep.Name,
                            StepIndex = i,
                            RunbookVersion = runbook.Version,
                            WorkerId = initStep.WorkerId,
                            FunctionName = initStep.Function,
                            ParamsJson = initStep.Params.Count > 0
                                ? JsonSerializer.Serialize(ResolveInitParams(initStep.Params, batchId))
                                : null,
                            IsPollStep = initStep.Poll is not null,
                            PollIntervalSec = initStep.Poll is not null
                                ? _phaseEvaluator.ParseDurationSeconds(initStep.Poll.Interval) : null,
                            PollTimeoutSec = initStep.Poll is not null
                                ? _phaseEvaluator.ParseDurationSeconds(initStep.Poll.Timeout) : null,
                            OnFailure = initStep.OnFailure
                        }, transaction);
                    }
                }

                transaction.Commit();

                result.Success = true;
                result.BatchId = batchId;
                result.Status = hasInitSteps ? "pending_init" : "active";
                result.MemberCount = memberData.Rows.Count;
                result.AvailablePhases = definition.Phases.Select(p => p.Name).ToList();

                _logger.LogInformation(
                    "Created manual batch {BatchId} for runbook {RunbookName} with {MemberCount} members",
                    batchId, runbook.Name, memberData.Rows.Count);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Failed to create manual batch for runbook {RunbookName}", runbook.Name);
        }

        return result;
    }

    public async Task<AdvanceResult> AdvanceBatchAsync(
        BatchRecord batch,
        RunbookRecord runbook,
        RunbookDefinition definition)
    {
        var result = new AdvanceResult();

        try
        {
            if (!batch.IsManual)
            {
                result.ErrorMessage = "Batch is not a manual batch";
                return result;
            }

            // Get init executions status
            var initExecs = await _initRepo.GetByBatchAsync(batch.Id);
            var hasInitSteps = initExecs.Any();

            // Check if init steps need to be dispatched
            if (hasInitSteps && batch.Status == BatchStatus.Detected)
            {
                // Dispatch init steps
                if (_serviceBusClient != null)
                {
                    await PublishBatchInitAsync(batch, runbook, initExecs.Count());
                }
                await _batchRepo.SetInitDispatchedAsync(batch.Id);

                result.Success = true;
                result.Action = "init_dispatched";
                result.StepCount = initExecs.Count();
                return result;
            }

            // Check if init steps are still in progress
            if (hasInitSteps && batch.Status == BatchStatus.InitDispatched)
            {
                var pendingInits = initExecs.Where(i => i.Status is StepStatus.Pending or StepStatus.Dispatched or StepStatus.Polling);
                if (pendingInits.Any())
                {
                    result.ErrorMessage = "Init steps not yet completed";
                    return result;
                }

                // All inits done, move to active
                await _batchRepo.UpdateStatusAsync(batch.Id, BatchStatus.Active);
                batch.Status = BatchStatus.Active;
            }

            // Get phase executions
            var phases = (await _phaseRepo.GetByBatchAsync(batch.Id)).OrderBy(p => p.OffsetMinutes).ToList();

            // Find first pending phase
            var pendingPhase = phases.FirstOrDefault(p => p.Status == PhaseStatus.Pending);
            if (pendingPhase == null)
            {
                // All phases complete
                await _batchRepo.UpdateStatusAsync(batch.Id, BatchStatus.Completed);
                result.Success = true;
                result.Action = "completed";
                return result;
            }

            // Check if any earlier phase is still in progress
            var inProgressPhase = phases
                .Where(p => p.OffsetMinutes < pendingPhase.OffsetMinutes)
                .FirstOrDefault(p => p.Status == PhaseStatus.Dispatched);

            if (inProgressPhase != null)
            {
                result.ErrorMessage = $"Previous phase '{inProgressPhase.PhaseName}' still in progress";
                return result;
            }

            // Get active members
            var members = await _memberRepo.GetActiveByBatchAsync(batch.Id);
            var memberCount = members.Count();

            // Dispatch the phase via Service Bus if available
            if (_serviceBusClient != null)
            {
                await PublishPhaseDueAsync(pendingPhase, batch, runbook);
            }

            // Update phase status
            await _phaseRepo.SetDispatchedAsync(pendingPhase.Id);
            await _batchRepo.UpdateCurrentPhaseAsync(batch.Id, pendingPhase.PhaseName);

            // Determine next phase
            var nextPhase = phases
                .OrderBy(p => p.OffsetMinutes)
                .FirstOrDefault(p => p.OffsetMinutes > pendingPhase.OffsetMinutes && p.Status == PhaseStatus.Pending);

            result.Success = true;
            result.Action = "phase_dispatched";
            result.PhaseName = pendingPhase.PhaseName;
            result.MemberCount = memberCount;
            result.StepCount = definition.Phases
                .FirstOrDefault(p => p.Name == pendingPhase.PhaseName)?
                .Steps.Count ?? 0;
            result.NextPhase = nextPhase?.PhaseName;

            _logger.LogInformation(
                "Advanced manual batch {BatchId}: dispatched phase '{PhaseName}' for {MemberCount} members",
                batch.Id, pendingPhase.PhaseName, memberCount);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Failed to advance batch {BatchId}", batch.Id);
        }

        return result;
    }

    private async Task PublishBatchInitAsync(BatchRecord batch, RunbookRecord runbook, int memberCount)
    {
        await using var sender = _serviceBusClient!.CreateSender("orchestrator-events");
        var message = new BatchInitMessage
        {
            RunbookName = runbook.Name,
            RunbookVersion = runbook.Version,
            BatchId = batch.Id,
            BatchStartTime = DateTime.UtcNow, // Use current time for manual batches
            MemberCount = memberCount
        };

        var sbMessage = new ServiceBusMessage(JsonSerializer.Serialize(message))
        {
            ContentType = "application/json"
        };
        sbMessage.ApplicationProperties["MessageType"] = message.MessageType;

        await sender.SendMessageAsync(sbMessage);
    }

    private async Task PublishPhaseDueAsync(PhaseExecutionRecord phase, BatchRecord batch, RunbookRecord runbook)
    {
        await using var sender = _serviceBusClient!.CreateSender("orchestrator-events");
        var message = new PhaseDueMessage
        {
            PhaseExecutionId = phase.Id,
            PhaseName = phase.PhaseName,
            BatchId = batch.Id,
            RunbookName = runbook.Name,
            RunbookVersion = phase.RunbookVersion
        };

        var sbMessage = new ServiceBusMessage(JsonSerializer.Serialize(message))
        {
            ContentType = "application/json"
        };
        sbMessage.ApplicationProperties["MessageType"] = message.MessageType;

        await sender.SendMessageAsync(sbMessage);
    }

    private static Dictionary<string, string> ResolveInitParams(
        Dictionary<string, string> paramTemplates, int batchId)
    {
        var resolved = new Dictionary<string, string>();
        foreach (var (key, template) in paramTemplates)
        {
            var value = template
                .Replace("{{_batch_id}}", batchId.ToString())
                .Replace("{{_batch_start_time}}", DateTime.UtcNow.ToString("o"));
            resolved[key] = value;
        }
        return resolved;
    }
}
