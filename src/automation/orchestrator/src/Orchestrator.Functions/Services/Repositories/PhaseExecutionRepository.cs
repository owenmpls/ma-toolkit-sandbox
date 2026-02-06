using System.Data;
using Dapper;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Services;

namespace Orchestrator.Functions.Services.Repositories;

public interface IPhaseExecutionRepository
{
    Task<PhaseExecutionRecord?> GetByIdAsync(int id);
    Task<IEnumerable<PhaseExecutionRecord>> GetByBatchAsync(int batchId);
    Task<IEnumerable<PhaseExecutionRecord>> GetDispatchedByBatchAsync(int batchId);
    Task UpdateStatusAsync(int id, string status);
    Task SetCompletedAsync(int id);
    Task SetFailedAsync(int id);
    Task<int> InsertAsync(PhaseExecutionRecord record, IDbTransaction? transaction = null);
}

public class PhaseExecutionRepository : IPhaseExecutionRepository
{
    private readonly IDbConnectionFactory _db;

    public PhaseExecutionRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<PhaseExecutionRecord?> GetByIdAsync(int id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<PhaseExecutionRecord>(
            "SELECT * FROM phase_executions WHERE id = @Id",
            new { Id = id });
    }

    public async Task<IEnumerable<PhaseExecutionRecord>> GetByBatchAsync(int batchId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<PhaseExecutionRecord>(
            "SELECT * FROM phase_executions WHERE batch_id = @BatchId",
            new { BatchId = batchId });
    }

    public async Task<IEnumerable<PhaseExecutionRecord>> GetDispatchedByBatchAsync(int batchId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<PhaseExecutionRecord>(
            $"SELECT * FROM phase_executions WHERE batch_id = @BatchId AND status IN ('{PhaseStatus.Dispatched}', '{PhaseStatus.Completed}')",
            new { BatchId = batchId });
    }

    public async Task UpdateStatusAsync(int id, string status)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE phase_executions SET status = @Status WHERE id = @Id",
            new { Id = id, Status = status });
    }

    public async Task SetCompletedAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            $"UPDATE phase_executions SET status = '{PhaseStatus.Completed}', completed_at = SYSUTCDATETIME() WHERE id = @Id",
            new { Id = id });
    }

    public async Task SetFailedAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            $"UPDATE phase_executions SET status = '{PhaseStatus.Failed}', completed_at = SYSUTCDATETIME() WHERE id = @Id",
            new { Id = id });
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
}
