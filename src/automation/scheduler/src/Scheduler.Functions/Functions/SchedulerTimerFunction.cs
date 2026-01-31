using System.Data;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Scheduler.Functions.Models.Db;
using Scheduler.Functions.Models.Messages;
using Scheduler.Functions.Models.Yaml;
using Scheduler.Functions.Services;

namespace Scheduler.Functions.Functions;

public class SchedulerTimerFunction
{
    private readonly IRunbookRepository _runbookRepo;
    private readonly IBatchRepository _batchRepo;
    private readonly IMemberRepository _memberRepo;
    private readonly IPhaseExecutionRepository _phaseRepo;
    private readonly IStepExecutionRepository _stepRepo;
    private readonly IInitExecutionRepository _initRepo;
    private readonly IDynamicTableManager _dynamicTableManager;
    private readonly IDataSourceQueryService _dataSourceQuery;
    private readonly IRunbookParser _parser;
    private readonly IMemberDiffService _memberDiff;
    private readonly IPhaseEvaluator _phaseEvaluator;
    private readonly ITemplateResolver _templateResolver;
    private readonly IServiceBusPublisher _publisher;
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<SchedulerTimerFunction> _logger;

    public SchedulerTimerFunction(
        IRunbookRepository runbookRepo,
        IBatchRepository batchRepo,
        IMemberRepository memberRepo,
        IPhaseExecutionRepository phaseRepo,
        IStepExecutionRepository stepRepo,
        IInitExecutionRepository initRepo,
        IDynamicTableManager dynamicTableManager,
        IDataSourceQueryService dataSourceQuery,
        IRunbookParser parser,
        IMemberDiffService memberDiff,
        IPhaseEvaluator phaseEvaluator,
        ITemplateResolver templateResolver,
        IServiceBusPublisher publisher,
        IDbConnectionFactory db,
        ILogger<SchedulerTimerFunction> logger)
    {
        _runbookRepo = runbookRepo;
        _batchRepo = batchRepo;
        _memberRepo = memberRepo;
        _phaseRepo = phaseRepo;
        _stepRepo = stepRepo;
        _initRepo = initRepo;
        _dynamicTableManager = dynamicTableManager;
        _dataSourceQuery = dataSourceQuery;
        _parser = parser;
        _memberDiff = memberDiff;
        _phaseEvaluator = phaseEvaluator;
        _templateResolver = templateResolver;
        _publisher = publisher;
        _db = db;
        _logger = logger;
    }

    [Function("SchedulerTimer")]
    public async Task RunAsync([TimerTrigger("0 */5 * * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Scheduler timer triggered at {Time}", DateTime.UtcNow);

        var runbooks = await _runbookRepo.GetActiveRunbooksAsync();

        foreach (var runbook in runbooks)
        {
            try
            {
                await ProcessRunbookAsync(runbook);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing runbook {RunbookName} v{Version}",
                    runbook.Name, runbook.Version);
            }
        }

        // Check polling steps across all runbooks
        await CheckPollingStepsAsync();

        _logger.LogInformation("Scheduler timer completed");
    }

    private async Task ProcessRunbookAsync(RunbookRecord runbook)
    {
        _logger.LogInformation("Processing runbook {RunbookName} v{Version}", runbook.Name, runbook.Version);

        // Parse YAML
        var definition = _parser.Parse(runbook.YamlContent);

        // Execute data source query
        DataTable queryResults;
        try
        {
            queryResults = await _dataSourceQuery.ExecuteAsync(definition.DataSource);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute data source query for runbook {RunbookName}", runbook.Name);
            return;
        }

        if (queryResults.Rows.Count == 0)
        {
            _logger.LogInformation("No results from data source for runbook {RunbookName}", runbook.Name);
            return;
        }

        // Ensure dynamic data table exists
        var queryColumns = queryResults.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
        await _dynamicTableManager.EnsureTableAsync(
            runbook.DataTableName,
            queryColumns,
            definition.DataSource.MultiValuedColumns);

        // Upsert query results into dynamic table
        await _dynamicTableManager.UpsertDataAsync(
            runbook.DataTableName,
            definition.DataSource.PrimaryKey,
            definition.DataSource.BatchTimeColumn,
            queryResults,
            definition.DataSource.MultiValuedColumns);

        // Group by batch time
        var batchGroups = GroupByBatchTime(queryResults, definition.DataSource);

        var now = DateTime.UtcNow;

        foreach (var (batchTime, rows) in batchGroups)
        {
            await ProcessBatchGroupAsync(runbook, definition, batchTime, rows, now);
        }

        // Evaluate pending phases for all active batches
        var activeBatches = await _batchRepo.GetActiveByRunbookAsync(runbook.Id);
        foreach (var batch in activeBatches)
        {
            await EvaluatePendingPhasesAsync(runbook, definition, batch, now);

            // Handle version transitions
            await HandleVersionTransitionAsync(runbook, definition, batch, now);
        }
    }

    private Dictionary<DateTime, List<DataRow>> GroupByBatchTime(DataTable results, DataSourceConfig config)
    {
        var groups = new Dictionary<DateTime, List<DataRow>>();

        bool isImmediate = string.Equals(config.BatchTime, "immediate", StringComparison.OrdinalIgnoreCase);

        foreach (DataRow row in results.Rows)
        {
            DateTime batchTime;

            if (isImmediate)
            {
                // For immediate runbooks, use a rounded current UTC time as the batch time
                // Round to the nearest 5-minute interval for grouping
                var now = DateTime.UtcNow;
                batchTime = new DateTime(now.Year, now.Month, now.Day, now.Hour,
                    (now.Minute / 5) * 5, 0, DateTimeKind.Utc);
            }
            else
            {
                var timeValue = row[config.BatchTimeColumn!]?.ToString();
                if (string.IsNullOrEmpty(timeValue) || !DateTime.TryParse(timeValue, out batchTime))
                {
                    _logger.LogWarning("Skipping row with invalid batch time: {TimeValue}", timeValue);
                    continue;
                }
            }

            if (!groups.ContainsKey(batchTime))
                groups[batchTime] = new List<DataRow>();

            groups[batchTime].Add(row);
        }

        return groups;
    }

    private async Task ProcessBatchGroupAsync(
        RunbookRecord runbook, RunbookDefinition definition,
        DateTime batchTime, List<DataRow> rows, DateTime now)
    {
        var existingBatch = await _batchRepo.GetByRunbookAndTimeAsync(runbook.Id, batchTime);

        if (existingBatch is null)
        {
            await CreateNewBatchAsync(runbook, definition, batchTime, rows, now);
        }
        else if (existingBatch.Status is not ("completed" or "failed"))
        {
            await ProcessExistingBatchAsync(runbook, definition, existingBatch, rows, now);
        }
    }

    private async Task CreateNewBatchAsync(
        RunbookRecord runbook, RunbookDefinition definition,
        DateTime batchTime, List<DataRow> rows, DateTime now)
    {
        _logger.LogInformation(
            "New batch detected for runbook {RunbookName}: {BatchTime} with {MemberCount} members",
            runbook.Name, batchTime, rows.Count);

        using var conn = _db.CreateConnection();
        conn.Open();
        using var transaction = ((SqlConnection)conn).BeginTransaction();

        try
        {
            // Insert batch
            var batchId = await _batchRepo.InsertAsync(new BatchRecord
            {
                RunbookId = runbook.Id,
                BatchStartTime = batchTime,
                Status = "detected"
            }, transaction);

            // Insert members
            var memberIds = new List<int>();
            foreach (var row in rows)
            {
                var memberKey = row[definition.DataSource.PrimaryKey]?.ToString() ?? string.Empty;
                var memberId = await _memberRepo.InsertAsync(new BatchMemberRecord
                {
                    BatchId = batchId,
                    MemberKey = memberKey
                }, transaction);
                memberIds.Add(memberId);
            }

            // Create phase executions
            var phaseExecs = _phaseEvaluator.CreatePhaseExecutions(
                batchId, batchTime, definition, runbook.Version);

            foreach (var phaseExec in phaseExecs)
            {
                await _phaseRepo.InsertAsync(phaseExec, transaction);
            }

            // Create init executions
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
                        ? JsonSerializer.Serialize(ResolveInitParams(initStep.Params, batchId, batchTime))
                        : null,
                    IsPollStep = initStep.Poll is not null,
                    PollIntervalSec = initStep.Poll is not null
                        ? _phaseEvaluator.ParseDurationSeconds(initStep.Poll.Interval) : null,
                    PollTimeoutSec = initStep.Poll is not null
                        ? _phaseEvaluator.ParseDurationSeconds(initStep.Poll.Timeout) : null,
                }, transaction);
            }

            transaction.Commit();

            // Dispatch batch-init if there are init steps
            if (definition.Init.Count > 0)
            {
                await _publisher.PublishBatchInitAsync(new BatchInitMessage
                {
                    RunbookName = runbook.Name,
                    RunbookVersion = runbook.Version,
                    BatchId = batchId,
                    BatchStartTime = batchTime,
                    MemberCount = rows.Count
                });
                await _batchRepo.SetInitDispatchedAsync(batchId);
            }
            else
            {
                // No init steps, set batch to active directly
                await _batchRepo.UpdateStatusAsync(batchId, "active");
            }
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private async Task ProcessExistingBatchAsync(
        RunbookRecord runbook, RunbookDefinition definition,
        BatchRecord batch, List<DataRow> rows, DateTime now)
    {
        var existingMembers = await _memberRepo.GetByBatchAsync(batch.Id);
        var currentKeys = rows
            .Select(r => r[definition.DataSource.PrimaryKey]?.ToString() ?? string.Empty)
            .ToList();

        // For immediate batches, skip members already in an active batch
        bool isImmediate = string.Equals(definition.DataSource.BatchTime, "immediate", StringComparison.OrdinalIgnoreCase);
        if (isImmediate)
        {
            var filteredKeys = new List<string>();
            foreach (var key in currentKeys)
            {
                if (!await _memberRepo.IsMemberInActiveBatchAsync(runbook.Id, key))
                    filteredKeys.Add(key);
            }
            currentKeys = filteredKeys;
        }

        var diff = _memberDiff.ComputeDiff(existingMembers, currentKeys);

        // Process added members
        foreach (var addedKey in diff.Added)
        {
            var memberId = await _memberRepo.InsertAsync(new BatchMemberRecord
            {
                BatchId = batch.Id,
                MemberKey = addedKey
            });

            await _publisher.PublishMemberAddedAsync(new MemberAddedMessage
            {
                RunbookName = runbook.Name,
                RunbookVersion = runbook.Version,
                BatchId = batch.Id,
                BatchMemberId = memberId,
                MemberKey = addedKey
            });
            await _memberRepo.SetAddDispatchedAsync(memberId);
        }

        // Process removed members
        foreach (var removedMember in diff.Removed)
        {
            await _memberRepo.MarkRemovedAsync(removedMember.Id);

            await _publisher.PublishMemberRemovedAsync(new MemberRemovedMessage
            {
                RunbookName = runbook.Name,
                RunbookVersion = runbook.Version,
                BatchId = batch.Id,
                BatchMemberId = removedMember.Id,
                MemberKey = removedMember.MemberKey
            });
            await _memberRepo.SetRemoveDispatchedAsync(removedMember.Id);
        }
    }

    private async Task EvaluatePendingPhasesAsync(
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
        }
    }

    private async Task HandleVersionTransitionAsync(
        RunbookRecord runbook, RunbookDefinition definition, BatchRecord batch, DateTime now)
    {
        var existingPhases = (await _phaseRepo.GetByBatchAsync(batch.Id)).ToList();

        // Check if there are phases from older versions only
        var hasOldVersionPhases = existingPhases.Any(p => p.RunbookVersion < runbook.Version);
        var hasCurrentVersionPhases = existingPhases.Any(p => p.RunbookVersion == runbook.Version);

        if (!hasOldVersionPhases || hasCurrentVersionPhases)
            return;

        _logger.LogInformation(
            "Version transition detected for batch {BatchId}: creating v{Version} phases",
            batch.Id, runbook.Version);

        var newPhases = _phaseEvaluator.HandleVersionTransition(
            existingPhases, batch.Id, batch.BatchStartTime,
            definition, runbook.Version,
            runbook.OverdueBehavior, runbook.IgnoreOverdueApplied);

        foreach (var phase in newPhases)
        {
            await _phaseRepo.InsertAsync(phase);
        }

        if (runbook.OverdueBehavior == "ignore" && !runbook.IgnoreOverdueApplied)
        {
            await _runbookRepo.SetIgnoreOverdueAppliedAsync(runbook.Id);
        }

        // Handle init rerun if configured
        if (runbook.RerunInit && definition.Init.Count > 0)
        {
            var existingInits = await _initRepo.GetByBatchAsync(batch.Id);
            var hasCurrentInits = existingInits.Any(i => i.RunbookVersion == runbook.Version);

            if (!hasCurrentInits)
            {
                for (int i = 0; i < definition.Init.Count; i++)
                {
                    var initStep = definition.Init[i];
                    await _initRepo.InsertAsync(new InitExecutionRecord
                    {
                        BatchId = batch.Id,
                        StepName = initStep.Name,
                        StepIndex = i,
                        RunbookVersion = runbook.Version,
                        WorkerId = initStep.WorkerId,
                        FunctionName = initStep.Function,
                        ParamsJson = initStep.Params.Count > 0
                            ? JsonSerializer.Serialize(ResolveInitParams(initStep.Params, batch.Id, batch.BatchStartTime))
                            : null,
                        IsPollStep = initStep.Poll is not null,
                        PollIntervalSec = initStep.Poll is not null
                            ? _phaseEvaluator.ParseDurationSeconds(initStep.Poll.Interval) : null,
                        PollTimeoutSec = initStep.Poll is not null
                            ? _phaseEvaluator.ParseDurationSeconds(initStep.Poll.Timeout) : null,
                    });
                }

                await _publisher.PublishBatchInitAsync(new BatchInitMessage
                {
                    RunbookName = runbook.Name,
                    RunbookVersion = runbook.Version,
                    BatchId = batch.Id,
                    BatchStartTime = batch.BatchStartTime,
                    MemberCount = (await _memberRepo.GetActiveByBatchAsync(batch.Id)).Count()
                });
            }
        }
    }

    private async Task CheckPollingStepsAsync()
    {
        var now = DateTime.UtcNow;

        // Check step_executions with polling due
        var pollingSteps = await _stepRepo.GetPollingStepsDueAsync(now);
        foreach (var step in pollingSteps)
        {
            // Get batch info via phase execution
            var phaseExecs = await _phaseRepo.GetByBatchAsync(0); // We need batch context
            // Load batch info from step's phase execution
            using var conn = _db.CreateConnection();
            var batchInfo = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<dynamic>(conn, @"
                SELECT b.id AS BatchId, b.runbook_id AS RunbookId, r.name AS RunbookName, r.version AS RunbookVersion
                FROM step_executions se
                JOIN phase_executions pe ON se.phase_execution_id = pe.id
                JOIN batches b ON pe.batch_id = b.id
                JOIN runbooks r ON b.runbook_id = r.id AND r.is_active = 1
                WHERE se.id = @StepId",
                new { StepId = step.Id });

            if (batchInfo is null) continue;

            await _publisher.PublishPollCheckAsync(new PollCheckMessage
            {
                RunbookName = (string)batchInfo.RunbookName,
                RunbookVersion = (int)batchInfo.RunbookVersion,
                BatchId = (int)batchInfo.BatchId,
                StepExecutionId = step.Id,
                StepName = step.StepName,
                PollCount = step.PollCount + 1
            });

            await _stepRepo.UpdateLastPolledAsync(step.Id);
        }

        // Check init_executions with polling due
        var pollingInits = await _initRepo.GetPollingStepsDueAsync(now);
        foreach (var init in pollingInits)
        {
            using var conn = _db.CreateConnection();
            var batchInfo = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<dynamic>(conn, @"
                SELECT b.id AS BatchId, r.name AS RunbookName, r.version AS RunbookVersion
                FROM init_executions ie
                JOIN batches b ON ie.batch_id = b.id
                JOIN runbooks r ON b.runbook_id = r.id AND r.is_active = 1
                WHERE ie.id = @InitId",
                new { InitId = init.Id });

            if (batchInfo is null) continue;

            await _publisher.PublishPollCheckAsync(new PollCheckMessage
            {
                RunbookName = (string)batchInfo.RunbookName,
                RunbookVersion = (int)batchInfo.RunbookVersion,
                BatchId = (int)batchInfo.BatchId,
                StepExecutionId = init.Id,
                StepName = init.StepName,
                PollCount = init.PollCount + 1
            });

            await _initRepo.UpdateLastPolledAsync(init.Id);
        }
    }

    private async Task<Dictionary<string, DataRow>> LoadMemberDataAsync(
        string tableName, List<BatchMemberRecord> members)
    {
        using var conn = _db.CreateConnection();
        conn.Open();

        var keys = members.Select(m => m.MemberKey).ToArray();
        var dataTable = new DataTable();

        // Use SqlDataAdapter to load into DataTable
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

    private Dictionary<string, string> ResolveInitParams(
        Dictionary<string, string> paramTemplates, int batchId, DateTime batchStartTime)
    {
        var resolved = new Dictionary<string, string>();
        foreach (var (key, template) in paramTemplates)
        {
            var value = template
                .Replace("{{_batch_id}}", batchId.ToString())
                .Replace("{{_batch_start_time}}", batchStartTime.ToString("o"));
            resolved[key] = value;
        }
        return resolved;
    }
}
