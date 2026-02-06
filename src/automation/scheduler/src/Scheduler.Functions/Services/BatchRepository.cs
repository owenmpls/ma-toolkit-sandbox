using System.Data;
using Dapper;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Services;

namespace Scheduler.Functions.Services;

public interface IBatchRepository
{
    Task<BatchRecord?> GetByRunbookAndTimeAsync(int runbookId, DateTime batchStartTime);
    Task<IEnumerable<BatchRecord>> GetActiveByRunbookAsync(int runbookId);
    Task<int> InsertAsync(BatchRecord record, IDbTransaction? transaction = null);
    Task UpdateStatusAsync(int id, string status);
    Task SetInitDispatchedAsync(int id);
}

public class BatchRepository : IBatchRepository
{
    private readonly IDbConnectionFactory _db;

    public BatchRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<BatchRecord?> GetByRunbookAndTimeAsync(int runbookId, DateTime batchStartTime)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<BatchRecord>(
            "SELECT * FROM batches WHERE runbook_id = @RunbookId AND batch_start_time = @BatchStartTime",
            new { RunbookId = runbookId, BatchStartTime = batchStartTime });
    }

    public async Task<IEnumerable<BatchRecord>> GetActiveByRunbookAsync(int runbookId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<BatchRecord>(
            @"SELECT * FROM batches WHERE runbook_id = @RunbookId
              AND status NOT IN (@Completed, @Failed)",
            new { RunbookId = runbookId, Completed = BatchStatus.Completed, Failed = BatchStatus.Failed });
    }

    public async Task<int> InsertAsync(BatchRecord record, IDbTransaction? transaction = null)
    {
        var conn = transaction?.Connection ?? _db.CreateConnection();
        try
        {
            return await conn.QuerySingleAsync<int>(@"
                INSERT INTO batches (runbook_id, batch_start_time, status)
                VALUES (@RunbookId, @BatchStartTime, @Status);
                SELECT CAST(SCOPE_IDENTITY() AS INT);",
                record, transaction);
        }
        finally
        {
            if (transaction is null && conn is IDisposable d)
                d.Dispose();
        }
    }

    public async Task UpdateStatusAsync(int id, string status)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE batches SET status = @Status WHERE id = @Id",
            new { Id = id, Status = status });
    }

    public async Task SetInitDispatchedAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            @"UPDATE batches SET status = @Status, init_dispatched_at = SYSUTCDATETIME() WHERE id = @Id",
            new { Id = id, Status = BatchStatus.InitDispatched });
    }
}
