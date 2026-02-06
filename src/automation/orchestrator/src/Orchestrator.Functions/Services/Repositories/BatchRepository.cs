using Dapper;
using Orchestrator.Functions.Models.Db;

namespace Orchestrator.Functions.Services.Repositories;

public interface IBatchRepository
{
    Task<BatchRecord?> GetByIdAsync(int id);
    Task UpdateStatusAsync(int id, string status);
    Task SetActiveAsync(int id);
    Task SetCompletedAsync(int id);
    Task SetFailedAsync(int id);
}

public class BatchRepository : IBatchRepository
{
    private readonly IDbConnectionFactory _db;

    public BatchRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<BatchRecord?> GetByIdAsync(int id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<BatchRecord>(
            "SELECT * FROM batches WHERE id = @Id",
            new { Id = id });
    }

    public async Task UpdateStatusAsync(int id, string status)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE batches SET status = @Status WHERE id = @Id",
            new { Id = id, Status = status });
    }

    public async Task SetActiveAsync(int id)
    {
        await UpdateStatusAsync(id, "active");
    }

    public async Task SetCompletedAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE batches SET status = 'completed' WHERE id = @Id",
            new { Id = id });
    }

    public async Task SetFailedAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE batches SET status = 'failed' WHERE id = @Id",
            new { Id = id });
    }
}
