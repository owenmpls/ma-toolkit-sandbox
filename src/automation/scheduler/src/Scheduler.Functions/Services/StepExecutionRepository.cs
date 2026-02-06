using System.Data;
using Dapper;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Services;

namespace Scheduler.Functions.Services;

public interface IStepExecutionRepository
{
    Task<IEnumerable<StepExecutionRecord>> GetByPhaseExecutionAsync(int phaseExecutionId);
    Task<IEnumerable<StepExecutionRecord>> GetPollingStepsDueAsync(DateTime now);
    Task<int> InsertAsync(StepExecutionRecord record, IDbTransaction? transaction = null);
    Task UpdateLastPolledAsync(int id);
}

public class StepExecutionRepository : IStepExecutionRepository
{
    private readonly IDbConnectionFactory _db;

    public StepExecutionRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IEnumerable<StepExecutionRecord>> GetByPhaseExecutionAsync(int phaseExecutionId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<StepExecutionRecord>(
            "SELECT * FROM step_executions WHERE phase_execution_id = @PhaseExecutionId",
            new { PhaseExecutionId = phaseExecutionId });
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
                    is_poll_step, poll_interval_sec, poll_timeout_sec)
                VALUES (
                    @PhaseExecutionId, @BatchMemberId, @StepName, @StepIndex,
                    @WorkerId, @FunctionName, @ParamsJson, @Status,
                    @IsPollStep, @PollIntervalSec, @PollTimeoutSec);
                SELECT CAST(SCOPE_IDENTITY() AS INT);",
                new
                {
                    record.PhaseExecutionId,
                    record.BatchMemberId,
                    record.StepName,
                    record.StepIndex,
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
            UPDATE step_executions
            SET last_polled_at = SYSUTCDATETIME(), poll_count = poll_count + 1
            WHERE id = @Id",
            new { Id = id });
    }
}
