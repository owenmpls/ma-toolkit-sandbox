using System.Data;
using System.Text.Json;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Scheduler.Functions.Services;

public interface IBatchDetector
{
    Task<Dictionary<DateTime, List<DataRow>>> GroupByBatchTimeAsync(
        DataTable queryResults, DataSourceConfig config);
    Task ProcessBatchGroupAsync(
        RunbookRecord runbook, RunbookDefinition definition,
        DateTime batchTime, List<DataRow> rows, DateTime now);
}

public class BatchDetector : IBatchDetector
{
    private readonly IBatchRepository _batchRepo;
    private readonly IMemberRepository _memberRepo;
    private readonly IPhaseExecutionRepository _phaseRepo;
    private readonly IInitExecutionRepository _initRepo;
    private readonly IPhaseEvaluator _phaseEvaluator;
    private readonly IServiceBusPublisher _publisher;
    private readonly IDbConnectionFactory _db;
    private readonly IMemberSynchronizer _memberSynchronizer;
    private readonly ILogger<BatchDetector> _logger;

    public BatchDetector(
        IBatchRepository batchRepo,
        IMemberRepository memberRepo,
        IPhaseExecutionRepository phaseRepo,
        IInitExecutionRepository initRepo,
        IPhaseEvaluator phaseEvaluator,
        IServiceBusPublisher publisher,
        IDbConnectionFactory db,
        IMemberSynchronizer memberSynchronizer,
        ILogger<BatchDetector> logger)
    {
        _batchRepo = batchRepo;
        _memberRepo = memberRepo;
        _phaseRepo = phaseRepo;
        _initRepo = initRepo;
        _phaseEvaluator = phaseEvaluator;
        _publisher = publisher;
        _db = db;
        _memberSynchronizer = memberSynchronizer;
        _logger = logger;
    }

    public Task<Dictionary<DateTime, List<DataRow>>> GroupByBatchTimeAsync(
        DataTable results, DataSourceConfig config)
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

        return Task.FromResult(groups);
    }

    public async Task ProcessBatchGroupAsync(
        RunbookRecord runbook, RunbookDefinition definition,
        DateTime batchTime, List<DataRow> rows, DateTime now)
    {
        var existingBatch = await _batchRepo.GetByRunbookNameAndTimeAsync(runbook.Name, batchTime);

        if (existingBatch is null)
        {
            await CreateNewBatchAsync(runbook, definition, batchTime, rows, now);
        }
        else if (existingBatch.Status is not (BatchStatus.Completed or BatchStatus.Failed))
        {
            await _memberSynchronizer.ProcessExistingBatchAsync(runbook, definition, existingBatch, rows, now);
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
                Status = BatchStatus.Detected
            }, transaction);

            // Insert members with point-in-time data snapshot
            var memberIds = new List<int>();
            var mvCols = definition.DataSource.MultiValuedColumns
                .ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var memberKey = row[definition.DataSource.PrimaryKey]?.ToString() ?? string.Empty;
                var memberId = await _memberRepo.InsertAsync(new BatchMemberRecord
                {
                    BatchId = batchId,
                    MemberKey = memberKey,
                    DataJson = MemberDataSerializer.Serialize(row, mvCols)
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
                var effectiveRetry = initStep.Retry ?? definition.Retry;
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
                    OnFailure = initStep.OnFailure,
                    MaxRetries = effectiveRetry?.MaxRetries,
                    RetryIntervalSec = effectiveRetry is { MaxRetries: > 0 }
                        ? _phaseEvaluator.ParseDurationSeconds(effectiveRetry.Interval) : null
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
                await _batchRepo.UpdateStatusAsync(batchId, BatchStatus.Active);
            }
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
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
