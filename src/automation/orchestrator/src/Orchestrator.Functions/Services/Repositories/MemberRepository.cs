using Dapper;
using Orchestrator.Functions.Models.Db;

namespace Orchestrator.Functions.Services.Repositories;

public interface IMemberRepository
{
    Task<BatchMemberRecord?> GetByIdAsync(int id);
    Task<IEnumerable<BatchMemberRecord>> GetActiveByBatchAsync(int batchId);
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

    public async Task<IEnumerable<BatchMemberRecord>> GetActiveByBatchAsync(int batchId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<BatchMemberRecord>(
            "SELECT * FROM batch_members WHERE batch_id = @BatchId AND status = 'active'",
            new { BatchId = batchId });
    }
}
