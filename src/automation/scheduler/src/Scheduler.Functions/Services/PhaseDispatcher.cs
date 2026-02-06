using System.Data;
using System.Text.Json;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Scheduler.Functions.Services;

public interface IPhaseDispatcher
{
    Task EvaluatePendingPhasesAsync(
        RunbookRecord runbook, RunbookDefinition definition, BatchRecord batch, DateTime now);
}

public class PhaseDispatcher : IPhaseDispatcher
{
    private readonly IPhaseExecutionRepository _phaseRepo;
    private readonly IMemberRepository _memberRepo;
    private readonly IStepExecutionRepository _stepRepo;
    private readonly IPhaseEvaluator _phaseEvaluator;
    private readonly ITemplateResolver _templateResolver;
    private readonly IServiceBusPublisher _publisher;
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<PhaseDispatcher> _logger;

    public PhaseDispatcher(
        IPhaseExecutionRepository phaseRepo,
        IMemberRepository memberRepo,
        IStepExecutionRepository stepRepo,
        IPhaseEvaluator phaseEvaluator,
        ITemplateResolver templateResolver,
        IServiceBusPublisher publisher,
        IDbConnectionFactory db,
        ILogger<PhaseDispatcher> logger)
    {
        _phaseRepo = phaseRepo;
        _memberRepo = memberRepo;
        _stepRepo = stepRepo;
        _phaseEvaluator = phaseEvaluator;
        _templateResolver = templateResolver;
        _publisher = publisher;
        _db = db;
        _logger = logger;
    }

    public async Task EvaluatePendingPhasesAsync(
        RunbookRecord runbook, RunbookDefinition definition, BatchRecord batch, DateTime now)
    {
        var pendingPhases = await _phaseRepo.GetPendingDueAsync(batch.Id, now);

        foreach (var phase in pendingPhases)
        {
            var phaseDefinition = definition.Phases.FirstOrDefault(p => p.Name == phase.PhaseName);
            if (phaseDefinition is null)
            {
                _logger.LogWarning("Phase definition not found for '{PhaseName}' in runbook {RunbookName}",
                    phase.PhaseName, runbook.Name);
                continue;
            }

            // Get active members for this batch
            var members = (await _memberRepo.GetActiveByBatchAsync(batch.Id)).ToList();
            if (members.Count == 0)
            {
                _logger.LogInformation("No active members for phase '{PhaseName}' in batch {BatchId}",
                    phase.PhaseName, batch.Id);
                continue;
            }

            // Load member data from dynamic table
            var memberData = await LoadMemberDataAsync(runbook.DataTableName, members);

            // Pre-create step executions with resolved params
            using var conn = _db.CreateConnection();
            conn.Open();
            using var transaction = ((SqlConnection)conn).BeginTransaction();

            try
            {
                foreach (var member in members)
                {
                    if (!memberData.TryGetValue(member.MemberKey, out var dataRow))
                    {
                        _logger.LogWarning("No data found for member {MemberKey} in table {TableName}",
                            member.MemberKey, runbook.DataTableName);
                        continue;
                    }

                    for (int i = 0; i < phaseDefinition.Steps.Count; i++)
                    {
                        var step = phaseDefinition.Steps[i];
                        var resolvedParams = _templateResolver.ResolveParams(
                            step.Params, dataRow, batch.Id, batch.BatchStartTime);
                        var resolvedFunction = _templateResolver.ResolveString(
                            step.Function, dataRow, batch.Id, batch.BatchStartTime);

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
                            OnFailure = step.OnFailure
                        }, transaction);
                    }
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            // Dispatch phase-due message
            await _publisher.PublishPhaseDueAsync(new PhaseDueMessage
            {
                RunbookName = runbook.Name,
                RunbookVersion = runbook.Version,
                BatchId = batch.Id,
                PhaseExecutionId = phase.Id,
                PhaseName = phase.PhaseName,
                OffsetMinutes = phase.OffsetMinutes,
                DueAt = phase.DueAt,
                MemberIds = members.Select(m => m.Id).ToList()
            });

            await _phaseRepo.SetDispatchedAsync(phase.Id);

            _logger.LogInformation(
                "Dispatched phase '{PhaseName}' for batch {BatchId} with {MemberCount} members",
                phase.PhaseName, batch.Id, members.Count);
        }
    }

    private async Task<Dictionary<string, DataRow>> LoadMemberDataAsync(
        string tableName, List<BatchMemberRecord> members)
    {
        using var conn = _db.CreateConnection();
        conn.Open();

        var keys = members.Select(m => m.MemberKey).ToArray();
        var dataTable = new DataTable();

        // Use Dapper to load data
        var sql = $"SELECT * FROM [{tableName}] WHERE _member_key IN @Keys AND _is_current = 1";
        var rows = await Dapper.SqlMapper.QueryAsync(conn, sql, new { Keys = keys });

        var result = new Dictionary<string, DataRow>();

        // Build a DataTable from the dynamic results
        var rowList = rows.ToList();
        if (rowList.Count == 0) return result;

        var firstRow = (IDictionary<string, object>)rowList[0];
        foreach (var key in firstRow.Keys)
        {
            dataTable.Columns.Add(key, typeof(string));
        }

        foreach (var row in rowList)
        {
            var dict = (IDictionary<string, object>)row;
            var dataRow = dataTable.NewRow();
            foreach (var kvp in dict)
            {
                dataRow[kvp.Key] = kvp.Value?.ToString() ?? (object)DBNull.Value;
            }
            dataTable.Rows.Add(dataRow);

            var memberKey = dict["_member_key"]?.ToString() ?? string.Empty;
            result[memberKey] = dataRow;
        }

        return result;
    }
}
