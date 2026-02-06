using Dapper;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Services;

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
            $"SELECT * FROM batch_members WHERE batch_id = @BatchId AND status = '{MemberStatus.Active}'",
            new { BatchId = batchId });
    }
}
