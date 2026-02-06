using System.Data;
using Dapper;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Services;

namespace Scheduler.Functions.Services;

public interface IInitExecutionRepository
{
    Task<IEnumerable<InitExecutionRecord>> GetByBatchAsync(int batchId);
    Task<IEnumerable<InitExecutionRecord>> GetPollingStepsDueAsync(DateTime now);
    Task<int> InsertAsync(InitExecutionRecord record, IDbTransaction? transaction = null);
    Task UpdateLastPolledAsync(int id);
}

public class InitExecutionRepository : IInitExecutionRepository
{
    private readonly IDbConnectionFactory _db;

    public InitExecutionRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IEnumerable<InitExecutionRecord>> GetByBatchAsync(int batchId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<InitExecutionRecord>(
            "SELECT * FROM init_executions WHERE batch_id = @BatchId",
            new { BatchId = batchId });
    }

    public async Task<IEnumerable<InitExecutionRecord>> GetPollingStepsDueAsync(DateTime now)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<InitExecutionRecord>(@"
            SELECT ie.* FROM init_executions ie
            JOIN batches b ON ie.batch_id = b.id
            WHERE ie.status = @Status
              AND ie.is_poll_step = 1
              AND DATEADD(SECOND, ie.poll_interval_sec, ie.last_polled_at) <= @Now",
            new { Status = StepStatus.Polling, Now = now });
    }

    public async Task<int> InsertAsync(InitExecutionRecord record, IDbTransaction? transaction = null)
    {
        var conn = transaction?.Connection ?? _db.CreateConnection();
        try
        {
            return await conn.QuerySingleAsync<int>(@"
                INSERT INTO init_executions (
                    batch_id, step_name, step_index, runbook_version,
                    worker_id, function_name, params_json, status,
                    is_poll_step, poll_interval_sec, poll_timeout_sec)
                VALUES (
                    @BatchId, @StepName, @StepIndex, @RunbookVersion,
                    @WorkerId, @FunctionName, @ParamsJson, @Status,
                    @IsPollStep, @PollIntervalSec, @PollTimeoutSec);
                SELECT CAST(SCOPE_IDENTITY() AS INT);",
                new
                {
                    record.BatchId,
                    record.StepName,
                    record.StepIndex,
                    record.RunbookVersion,
                    record.WorkerId,
                    record.FunctionName,
                    record.ParamsJson,
                    Status = StepStatus.Pending,
                    record.IsPollStep,
                    record.PollIntervalSec,
                    record.PollTimeoutSec
                }, transaction);
        }
        finally
        {
            if (transaction is null && conn is IDisposable d)
                d.Dispose();
        }
    }

    public async Task UpdateLastPolledAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE init_executions
            SET last_polled_at = SYSUTCDATETIME(), poll_count = poll_count + 1
            WHERE id = @Id",
            new { Id = id });
    }
}
