using System.Data;
using Dapper;
using Scheduler.Functions.Models.Db;

namespace Scheduler.Functions.Services;

public interface IMemberRepository
{
    Task<IEnumerable<BatchMemberRecord>> GetByBatchAsync(int batchId);
    Task<IEnumerable<BatchMemberRecord>> GetActiveByBatchAsync(int batchId);
    Task<int> InsertAsync(BatchMemberRecord record, IDbTransaction? transaction = null);
    Task MarkRemovedAsync(int id);
    Task SetAddDispatchedAsync(int id);
    Task SetRemoveDispatchedAsync(int id);
    Task<bool> IsMemberInActiveBatchAsync(int runbookId, string memberKey);
}

public class MemberRepository : IMemberRepository
{
    private readonly IDbConnectionFactory _db;

    public MemberRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IEnumerable<BatchMemberRecord>> GetByBatchAsync(int batchId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<BatchMemberRecord>(
            "SELECT * FROM batch_members WHERE batch_id = @BatchId",
            new { BatchId = batchId });
    }

    public async Task<IEnumerable<BatchMemberRecord>> GetActiveByBatchAsync(int batchId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<BatchMemberRecord>(
            "SELECT * FROM batch_members WHERE batch_id = @BatchId AND status = 'active'",
            new { BatchId = batchId });
    }

    public async Task<int> InsertAsync(BatchMemberRecord record, IDbTransaction? transaction = null)
    {
        var conn = transaction?.Connection ?? _db.CreateConnection();
        try
        {
            return await conn.QuerySingleAsync<int>(@"
                INSERT INTO batch_members (batch_id, member_key, status)
                VALUES (@BatchId, @MemberKey, 'active');
                SELECT CAST(SCOPE_IDENTITY() AS INT);",
                record, transaction);
        }
        finally
        {
            if (transaction is null && conn is IDisposable d)
                d.Dispose();
        }
    }

    public async Task MarkRemovedAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE batch_members SET status = 'removed', removed_at = SYSUTCDATETIME() WHERE id = @Id",
            new { Id = id });
    }

    public async Task SetAddDispatchedAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE batch_members SET add_dispatched_at = SYSUTCDATETIME() WHERE id = @Id",
            new { Id = id });
    }

    public async Task SetRemoveDispatchedAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE batch_members SET remove_dispatched_at = SYSUTCDATETIME() WHERE id = @Id",
            new { Id = id });
    }

    public async Task<bool> IsMemberInActiveBatchAsync(int runbookId, string memberKey)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<bool>(@"
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM batch_members bm
                JOIN batches b ON bm.batch_id = b.id
                WHERE b.runbook_id = @RunbookId
                  AND bm.member_key = @MemberKey
                  AND bm.status = 'active'
                  AND b.status NOT IN ('completed', 'failed')
            ) THEN 1 ELSE 0 END",
            new { RunbookId = runbookId, MemberKey = memberKey });
    }
}
