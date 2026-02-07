using Dapper;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Services;

namespace Orchestrator.Functions.Services.Repositories;

public interface IBatchRepository
{
    Task<BatchRecord?> GetByIdAsync(int id);
    Task UpdateStatusAsync(int id, string status);
    Task SetActiveAsync(int id);
    Task<bool> SetCompletedAsync(int id);
    Task<bool> SetFailedAsync(int id);
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
        await UpdateStatusAsync(id, BatchStatus.Active);
    }

    public async Task<bool> SetCompletedAsync(int id)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(
            "UPDATE batches SET status = @Status WHERE id = @Id AND status = @ExpectedStatus",
            new { Id = id, Status = BatchStatus.Completed, ExpectedStatus = BatchStatus.Active });
        return rows > 0;
    }

    public async Task<bool> SetFailedAsync(int id)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(
            "UPDATE batches SET status = @Status WHERE id = @Id AND status NOT IN (@Completed, @Failed)",
            new { Id = id, Status = BatchStatus.Failed, Completed = BatchStatus.Completed, Failed = BatchStatus.Failed });
        return rows > 0;
    }
}
