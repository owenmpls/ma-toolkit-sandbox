using System.Data;
using Dapper;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Services;

namespace AdminApi.Functions.Services.Repositories;

public interface IMemberRepository
{
    Task<BatchMemberRecord?> GetByIdAsync(int id);
    Task<IEnumerable<BatchMemberRecord>> GetByBatchAsync(int batchId);
    Task<IEnumerable<BatchMemberRecord>> GetActiveByBatchAsync(int batchId);
    Task<int> InsertAsync(BatchMemberRecord record, IDbTransaction? transaction = null);
    Task MarkRemovedAsync(int id);
}

public class MemberRepository : IMemberRepository
{
    private readonly IDbConnectionFactory _db;

    public MemberRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<BatchMemberRecord?> GetByIdAsync(int id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<BatchMemberRecord>(
            "SELECT * FROM batch_members WHERE id = @Id",
            new { Id = id });
    }

    public async Task<IEnumerable<BatchMemberRecord>> GetByBatchAsync(int batchId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<BatchMemberRecord>(
            "SELECT * FROM batch_members WHERE batch_id = @BatchId ORDER BY id",
            new { BatchId = batchId });
    }

    public async Task<IEnumerable<BatchMemberRecord>> GetActiveByBatchAsync(int batchId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<BatchMemberRecord>(
            "SELECT * FROM batch_members WHERE batch_id = @BatchId AND status = @Status ORDER BY id",
            new { BatchId = batchId, Status = MemberStatus.Active });
    }

    public async Task<int> InsertAsync(BatchMemberRecord record, IDbTransaction? transaction = null)
    {
        var conn = transaction?.Connection ?? _db.CreateConnection();
        try
        {
            return await conn.QuerySingleAsync<int>(@"
                INSERT INTO batch_members (batch_id, member_key, status)
                VALUES (@BatchId, @MemberKey, @Status);
                SELECT CAST(SCOPE_IDENTITY() AS INT);",
                new { record.BatchId, record.MemberKey, Status = MemberStatus.Active }, transaction);
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
            "UPDATE batch_members SET status = @Status, removed_at = SYSUTCDATETIME() WHERE id = @Id",
            new { Id = id, Status = MemberStatus.Removed });
    }
}
