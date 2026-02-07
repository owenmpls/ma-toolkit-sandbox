using System.Data;
using Dapper;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Services;

namespace Orchestrator.Functions.Services.Repositories;

public interface IInitExecutionRepository
{
    Task<InitExecutionRecord?> GetByIdAsync(int id);
    Task<InitExecutionRecord?> GetByJobIdAsync(string jobId);
    Task<IEnumerable<InitExecutionRecord>> GetByBatchAsync(int batchId);
    Task<IEnumerable<InitExecutionRecord>> GetPendingByBatchAsync(int batchId);
    Task<int> InsertAsync(InitExecutionRecord record, IDbTransaction? transaction = null);

    // Status updates
    Task<bool> SetDispatchedAsync(int id, string jobId);
    Task<bool> SetSucceededAsync(int id, string? resultJson);
    Task<bool> SetFailedAsync(int id, string errorMessage);
    Task<bool> SetPollingAsync(int id);
    Task<bool> SetPollTimeoutAsync(int id);
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
            "SELECT * FROM init_executions WHERE batch_id = @BatchId AND status = @Status ORDER BY step_index",
            new { BatchId = batchId, Status = StepStatus.Pending });
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
                    @WorkerId, @FunctionName, @ParamsJson, @Status,
                    @IsPollStep, @PollIntervalSec, @PollTimeoutSec, @OnFailure);
                SELECT CAST(SCOPE_IDENTITY() AS INT);",
                new
                {
                    record.BatchId, record.StepName, record.StepIndex, record.RunbookVersion,
                    record.WorkerId, record.FunctionName, record.ParamsJson, Status = StepStatus.Pending,
                    record.IsPollStep, record.PollIntervalSec, record.PollTimeoutSec, record.OnFailure
                }, transaction);
        }
        finally
        {
            if (transaction is null && conn is IDisposable d)
                d.Dispose();
        }
    }

    public async Task<bool> SetDispatchedAsync(int id, string jobId)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(@"
            UPDATE init_executions
            SET status = @Status, job_id = @JobId, dispatched_at = SYSUTCDATETIME()
            WHERE id = @Id AND status = @ExpectedStatus",
            new { Id = id, JobId = jobId, Status = StepStatus.Dispatched, ExpectedStatus = StepStatus.Pending });
        return rows > 0;
    }

    public async Task<bool> SetSucceededAsync(int id, string? resultJson)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(@"
            UPDATE init_executions
            SET status = @Status, result_json = @ResultJson, completed_at = SYSUTCDATETIME()
            WHERE id = @Id AND status IN (@Dispatched, @Polling)",
            new { Id = id, ResultJson = resultJson, Status = StepStatus.Succeeded, Dispatched = StepStatus.Dispatched, Polling = StepStatus.Polling });
        return rows > 0;
    }

    public async Task<bool> SetFailedAsync(int id, string errorMessage)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(@"
            UPDATE init_executions
            SET status = @Status, error_message = @ErrorMessage, completed_at = SYSUTCDATETIME()
            WHERE id = @Id AND status IN (@Dispatched, @Polling)",
            new { Id = id, ErrorMessage = errorMessage, Status = StepStatus.Failed, Dispatched = StepStatus.Dispatched, Polling = StepStatus.Polling });
        return rows > 0;
    }

    public async Task<bool> SetPollingAsync(int id)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(@"
            UPDATE init_executions
            SET status = @Status,
                poll_started_at = COALESCE(poll_started_at, SYSUTCDATETIME()),
                last_polled_at = SYSUTCDATETIME()
            WHERE id = @Id AND status IN (@Dispatched, @Polling)",
            new { Id = id, Status = StepStatus.Polling, Dispatched = StepStatus.Dispatched, Polling = StepStatus.Polling });
        return rows > 0;
    }

    public async Task<bool> SetPollTimeoutAsync(int id)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(@"
            UPDATE init_executions
            SET status = @Status, completed_at = SYSUTCDATETIME()
            WHERE id = @Id AND status = @ExpectedStatus",
            new { Id = id, Status = StepStatus.PollTimeout, ExpectedStatus = StepStatus.Polling });
        return rows > 0;
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
