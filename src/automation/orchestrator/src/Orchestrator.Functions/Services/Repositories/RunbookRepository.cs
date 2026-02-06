using Dapper;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Services;

namespace Orchestrator.Functions.Services.Repositories;

public interface IRunbookRepository
{
    Task<RunbookRecord?> GetByNameAndVersionAsync(string name, int version);
}

public class RunbookRepository : IRunbookRepository
{
    private readonly IDbConnectionFactory _db;

    public RunbookRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<RunbookRecord?> GetByNameAndVersionAsync(string name, int version)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<RunbookRecord>(
            "SELECT * FROM runbooks WHERE name = @Name AND version = @Version",
            new { Name = name, Version = version });
    }
}
