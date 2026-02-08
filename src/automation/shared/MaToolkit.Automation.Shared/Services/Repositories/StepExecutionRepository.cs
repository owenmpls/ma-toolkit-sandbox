using System.Data;
using Dapper;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;

namespace MaToolkit.Automation.Shared.Services.Repositories;

public class StepExecutionRepository : IStepExecutionRepository
{
    private readonly IDbConnectionFactory _db;

    public StepExecutionRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<StepExecutionRecord?> GetByIdAsync(int id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<StepExecutionRecord>(
            "SELECT * FROM step_executions WHERE id = @Id",
            new { Id = id });
    }

    public async Task<StepExecutionRecord?> GetByJobIdAsync(string jobId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<StepExecutionRecord>(
            "SELECT * FROM step_executions WHERE job_id = @JobId",
            new { JobId = jobId });
    }

    public async Task<IEnumerable<StepExecutionRecord>> GetByBatchAsync(int batchId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<StepExecutionRecord>(@"
            SELECT se.* FROM step_executions se
            JOIN phase_executions pe ON se.phase_execution_id = pe.id
            WHERE pe.batch_id = @BatchId
            ORDER BY pe.offset_minutes, se.step_index, se.id",
            new { BatchId = batchId });
    }

    public async Task<IEnumerable<StepExecutionRecord>> GetByPhaseExecutionAsync(int phaseExecutionId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<StepExecutionRecord>(
            "SELECT * FROM step_executions WHERE phase_execution_id = @PhaseExecutionId ORDER BY step_index, id",
            new { PhaseExecutionId = phaseExecutionId });
    }

    public async Task<IEnumerable<StepExecutionRecord>> GetPendingByPhaseAndIndexAsync(int phaseExecutionId, int stepIndex)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<StepExecutionRecord>(
            "SELECT * FROM step_executions WHERE phase_execution_id = @PhaseExecutionId AND step_index = @StepIndex AND status = @Status",
            new { PhaseExecutionId = phaseExecutionId, StepIndex = stepIndex, Status = StepStatus.Pending });
    }

    public async Task<IEnumerable<StepExecutionRecord>> GetPendingByMemberAsync(int batchMemberId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<StepExecutionRecord>(
            "SELECT * FROM step_executions WHERE batch_member_id = @BatchMemberId AND status IN (@Pending, @Dispatched, @Polling)",
            new { BatchMemberId = batchMemberId, Pending = StepStatus.Pending, Dispatched = StepStatus.Dispatched, Polling = StepStatus.Polling });
    }

    public async Task<IEnumerable<StepExecutionRecord>> GetByPhaseAndMemberAsync(int phaseExecutionId, int batchMemberId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<StepExecutionRecord>(
            "SELECT * FROM step_executions WHERE phase_execution_id = @PhaseExecutionId AND batch_member_id = @BatchMemberId ORDER BY step_index",
            new { PhaseExecutionId = phaseExecutionId, BatchMemberId = batchMemberId });
    }

    public async Task<IEnumerable<StepExecutionRecord>> GetPollingStepsDueAsync(DateTime now)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<StepExecutionRecord>(@"
            SELECT se.* FROM step_executions se
            JOIN phase_executions pe ON se.phase_execution_id = pe.id
            JOIN batches b ON pe.batch_id = b.id
            WHERE se.status = @Status
              AND se.is_poll_step = 1
              AND DATEADD(SECOND, se.poll_interval_sec, se.last_polled_at) <= @Now",
            new { Status = StepStatus.Polling, Now = now });
    }

    public async Task<int> InsertAsync(StepExecutionRecord record, IDbTransaction? transaction = null)
    {
        var conn = transaction?.Connection ?? _db.CreateConnection();
        try
        {
            return await conn.QuerySingleAsync<int>(@"
                INSERT INTO step_executions (
                    phase_execution_id, batch_member_id, step_name, step_index,
                    worker_id, function_name, params_json, status,
                    is_poll_step, poll_interval_sec, poll_timeout_sec, on_failure,
                    max_retries, retry_interval_sec)
                VALUES (
                    @PhaseExecutionId, @BatchMemberId, @StepName, @StepIndex,
                    @WorkerId, @FunctionName, @ParamsJson, @Status,
                    @IsPollStep, @PollIntervalSec, @PollTimeoutSec, @OnFailure,
                    @MaxRetries, @RetryIntervalSec);
                SELECT CAST(SCOPE_IDENTITY() AS INT);",
                new
                {
                    record.PhaseExecutionId, record.BatchMemberId, record.StepName, record.StepIndex,
                    record.WorkerId, record.FunctionName, record.ParamsJson, Status = StepStatus.Pending,
                    record.IsPollStep, record.PollIntervalSec, record.PollTimeoutSec, record.OnFailure,
                    record.MaxRetries, record.RetryIntervalSec
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
            UPDATE step_executions
            SET status = @Status, job_id = @JobId, dispatched_at = SYSUTCDATETIME()
            WHERE id = @Id AND status = @ExpectedStatus",
            new { Id = id, JobId = jobId, Status = StepStatus.Dispatched, ExpectedStatus = StepStatus.Pending });
        return rows > 0;
    }

    public async Task<bool> SetSucceededAsync(int id, string? resultJson)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(@"
            UPDATE step_executions
            SET status = @Status, result_json = @ResultJson, completed_at = SYSUTCDATETIME()
            WHERE id = @Id AND status IN (@Dispatched, @Polling)",
            new { Id = id, ResultJson = resultJson, Status = StepStatus.Succeeded, Dispatched = StepStatus.Dispatched, Polling = StepStatus.Polling });
        return rows > 0;
    }

    public async Task<bool> SetFailedAsync(int id, string errorMessage)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(@"
            UPDATE step_executions
            SET status = @Status, error_message = @ErrorMessage, completed_at = SYSUTCDATETIME()
            WHERE id = @Id AND status IN (@Dispatched, @Polling)",
            new { Id = id, ErrorMessage = errorMessage, Status = StepStatus.Failed, Dispatched = StepStatus.Dispatched, Polling = StepStatus.Polling });
        return rows > 0;
    }

    public async Task<bool> SetPollingAsync(int id)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(@"
            UPDATE step_executions
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
            UPDATE step_executions
            SET status = @Status, completed_at = SYSUTCDATETIME()
            WHERE id = @Id AND status = @ExpectedStatus",
            new { Id = id, Status = StepStatus.PollTimeout, ExpectedStatus = StepStatus.Polling });
        return rows > 0;
    }

    public async Task<bool> SetCancelledAsync(int id)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(@"
            UPDATE step_executions
            SET status = @Status, completed_at = SYSUTCDATETIME()
            WHERE id = @Id AND status IN (@Pending, @Dispatched, @Polling)",
            new { Id = id, Status = StepStatus.Cancelled, Pending = StepStatus.Pending, Dispatched = StepStatus.Dispatched, Polling = StepStatus.Polling });
        return rows > 0;
    }

    public async Task<bool> SetRetryPendingAsync(int id, DateTime retryAfter)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(@"
            UPDATE step_executions
            SET status = @Status,
                retry_count = retry_count + 1,
                retry_after = @RetryAfter,
                completed_at = NULL,
                job_id = NULL
            WHERE id = @Id AND status IN (@Failed, @PollTimeout)",
            new { Id = id, Status = StepStatus.Pending, RetryAfter = retryAfter,
                  Failed = StepStatus.Failed, PollTimeout = StepStatus.PollTimeout });
        return rows > 0;
    }

    public async Task UpdatePollStateAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE step_executions
            SET last_polled_at = SYSUTCDATETIME(), poll_count = poll_count + 1
            WHERE id = @Id",
            new { Id = id });
    }
}
