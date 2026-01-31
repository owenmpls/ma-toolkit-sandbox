using System.Data;
using Dapper;
using Scheduler.Functions.Models.Db;

namespace Scheduler.Functions.Services;

public interface IPhaseExecutionRepository
{
    Task<IEnumerable<PhaseExecutionRecord>> GetByBatchAsync(int batchId);
    Task<IEnumerable<PhaseExecutionRecord>> GetPendingDueAsync(int batchId, DateTime now);
    Task<int> InsertAsync(PhaseExecutionRecord record, IDbTransaction? transaction = null);
    Task SetDispatchedAsync(int id);
    Task UpdateStatusAsync(int id, string status);
}

public class PhaseExecutionRepository : IPhaseExecutionRepository
{
    private readonly IDbConnectionFactory _db;

    public PhaseExecutionRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IEnumerable<PhaseExecutionRecord>> GetByBatchAsync(int batchId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<PhaseExecutionRecord>(
            "SELECT * FROM phase_executions WHERE batch_id = @BatchId",
            new { BatchId = batchId });
    }

    public async Task<IEnumerable<PhaseExecutionRecord>> GetPendingDueAsync(int batchId, DateTime now)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<PhaseExecutionRecord>(
            "SELECT * FROM phase_executions WHERE batch_id = @BatchId AND status = 'pending' AND due_at <= @Now",
            new { BatchId = batchId, Now = now });
    }

    public async Task<int> InsertAsync(PhaseExecutionRecord record, IDbTransaction? transaction = null)
    {
        var conn = transaction?.Connection ?? _db.CreateConnection();
        try
        {
            return await conn.QuerySingleAsync<int>(@"
                INSERT INTO phase_executions (batch_id, phase_name, offset_minutes, due_at, runbook_version, status)
                VALUES (@BatchId, @PhaseName, @OffsetMinutes, @DueAt, @RunbookVersion, @Status);
                SELECT CAST(SCOPE_IDENTITY() AS INT);",
                record, transaction);
        }
        finally
        {
            if (transaction is null && conn is IDisposable d)
                d.Dispose();
        }
    }

    public async Task SetDispatchedAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE phase_executions SET status = 'dispatched', dispatched_at = SYSUTCDATETIME() WHERE id = @Id",
            new { Id = id });
    }

    public async Task UpdateStatusAsync(int id, string status)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE phase_executions SET status = @Status WHERE id = @Id",
            new { Id = id, Status = status });
    }
}
