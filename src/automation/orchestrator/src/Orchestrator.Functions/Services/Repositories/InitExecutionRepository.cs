using System.Data;
using Dapper;
using Orchestrator.Functions.Models.Db;

namespace Orchestrator.Functions.Services.Repositories;

public interface IInitExecutionRepository
{
    Task<InitExecutionRecord?> GetByIdAsync(int id);
    Task<InitExecutionRecord?> GetByJobIdAsync(string jobId);
    Task<IEnumerable<InitExecutionRecord>> GetByBatchAsync(int batchId);
    Task<IEnumerable<InitExecutionRecord>> GetPendingByBatchAsync(int batchId);
    Task<int> InsertAsync(InitExecutionRecord record, IDbTransaction? transaction = null);

    // Status updates
    Task SetDispatchedAsync(int id, string jobId);
    Task SetSucceededAsync(int id, string? resultJson);
    Task SetFailedAsync(int id, string errorMessage);
    Task SetPollingAsync(int id);
    Task SetPollTimeoutAsync(int id);
    Task UpdatePollStateAsync(int id);
}

public class InitExecutionRepository : IInitExecutionRepository
{
    private readonly IDbConnectionFactory _db;

    public InitExecutionRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<InitExecutionRecord?> GetByIdAsync(int id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<InitExecutionRecord>(
            "SELECT * FROM init_executions WHERE id = @Id",
            new { Id = id });
    }

    public async Task<InitExecutionRecord?> GetByJobIdAsync(string jobId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<InitExecutionRecord>(
            "SELECT * FROM init_executions WHERE job_id = @JobId",
            new { JobId = jobId });
    }

    public async Task<IEnumerable<InitExecutionRecord>> GetByBatchAsync(int batchId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<InitExecutionRecord>(
            "SELECT * FROM init_executions WHERE batch_id = @BatchId ORDER BY step_index",
            new { BatchId = batchId });
    }

    public async Task<IEnumerable<InitExecutionRecord>> GetPendingByBatchAsync(int batchId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<InitExecutionRecord>(
            "SELECT * FROM init_executions WHERE batch_id = @BatchId AND status = 'pending' ORDER BY step_index",
            new { BatchId = batchId });
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
                    is_poll_step, poll_interval_sec, poll_timeout_sec, on_failure)
                VALUES (
                    @BatchId, @StepName, @StepIndex, @RunbookVersion,
                    @WorkerId, @FunctionName, @ParamsJson, 'pending',
                    @IsPollStep, @PollIntervalSec, @PollTimeoutSec, @OnFailure);
                SELECT CAST(SCOPE_IDENTITY() AS INT);",
                record, transaction);
        }
        finally
        {
            if (transaction is null && conn is IDisposable d)
                d.Dispose();
        }
    }

    public async Task SetDispatchedAsync(int id, string jobId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE init_executions
            SET status = 'dispatched', job_id = @JobId, dispatched_at = SYSUTCDATETIME()
            WHERE id = @Id",
            new { Id = id, JobId = jobId });
    }

    public async Task SetSucceededAsync(int id, string? resultJson)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE init_executions
            SET status = 'succeeded', result_json = @ResultJson, completed_at = SYSUTCDATETIME()
            WHERE id = @Id",
            new { Id = id, ResultJson = resultJson });
    }

    public async Task SetFailedAsync(int id, string errorMessage)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE init_executions
            SET status = 'failed', error_message = @ErrorMessage, completed_at = SYSUTCDATETIME()
            WHERE id = @Id",
            new { Id = id, ErrorMessage = errorMessage });
    }

    public async Task SetPollingAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE init_executions
            SET status = 'polling',
                poll_started_at = COALESCE(poll_started_at, SYSUTCDATETIME()),
                last_polled_at = SYSUTCDATETIME()
            WHERE id = @Id",
            new { Id = id });
    }

    public async Task SetPollTimeoutAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE init_executions
            SET status = 'poll_timeout', completed_at = SYSUTCDATETIME()
            WHERE id = @Id",
            new { Id = id });
    }

    public async Task UpdatePollStateAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE init_executions
            SET last_polled_at = SYSUTCDATETIME(), poll_count = poll_count + 1
            WHERE id = @Id",
            new { Id = id });
    }
}
