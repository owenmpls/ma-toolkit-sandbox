using System.Data;
using Dapper;
using Orchestrator.Functions.Models.Db;

namespace Orchestrator.Functions.Services.Repositories;

public interface IStepExecutionRepository
{
    Task<StepExecutionRecord?> GetByIdAsync(int id);
    Task<StepExecutionRecord?> GetByJobIdAsync(string jobId);
    Task<IEnumerable<StepExecutionRecord>> GetByPhaseExecutionAsync(int phaseExecutionId);
    Task<IEnumerable<StepExecutionRecord>> GetPendingByPhaseAndIndexAsync(int phaseExecutionId, int stepIndex);
    Task<IEnumerable<StepExecutionRecord>> GetPendingByMemberAsync(int batchMemberId);
    Task<int> InsertAsync(StepExecutionRecord record, IDbTransaction? transaction = null);

    // Status updates
    Task SetDispatchedAsync(int id, string jobId);
    Task SetSucceededAsync(int id, string? resultJson);
    Task SetFailedAsync(int id, string errorMessage);
    Task SetPollingAsync(int id);
    Task SetPollTimeoutAsync(int id);
    Task SetCancelledAsync(int id);
    Task UpdatePollStateAsync(int id);
}

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
            "SELECT * FROM step_executions WHERE phase_execution_id = @PhaseExecutionId AND step_index = @StepIndex AND status = 'pending'",
            new { PhaseExecutionId = phaseExecutionId, StepIndex = stepIndex });
    }

    public async Task<IEnumerable<StepExecutionRecord>> GetPendingByMemberAsync(int batchMemberId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<StepExecutionRecord>(
            "SELECT * FROM step_executions WHERE batch_member_id = @BatchMemberId AND status IN ('pending', 'dispatched')",
            new { BatchMemberId = batchMemberId });
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
                    is_poll_step, poll_interval_sec, poll_timeout_sec, on_failure)
                VALUES (
                    @PhaseExecutionId, @BatchMemberId, @StepName, @StepIndex,
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
            UPDATE step_executions
            SET status = 'dispatched', job_id = @JobId, dispatched_at = SYSUTCDATETIME()
            WHERE id = @Id",
            new { Id = id, JobId = jobId });
    }

    public async Task SetSucceededAsync(int id, string? resultJson)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE step_executions
            SET status = 'succeeded', result_json = @ResultJson, completed_at = SYSUTCDATETIME()
            WHERE id = @Id",
            new { Id = id, ResultJson = resultJson });
    }

    public async Task SetFailedAsync(int id, string errorMessage)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE step_executions
            SET status = 'failed', error_message = @ErrorMessage, completed_at = SYSUTCDATETIME()
            WHERE id = @Id",
            new { Id = id, ErrorMessage = errorMessage });
    }

    public async Task SetPollingAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE step_executions
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
            UPDATE step_executions
            SET status = 'poll_timeout', completed_at = SYSUTCDATETIME()
            WHERE id = @Id",
            new { Id = id });
    }

    public async Task SetCancelledAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE step_executions
            SET status = 'cancelled', completed_at = SYSUTCDATETIME()
            WHERE id = @Id",
            new { Id = id });
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
