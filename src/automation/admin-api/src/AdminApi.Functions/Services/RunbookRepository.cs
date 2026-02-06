using Dapper;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Services;

namespace AdminApi.Functions.Services;

public interface IRunbookRepository
{
    Task<IEnumerable<RunbookRecord>> GetActiveRunbooksAsync();
    Task<RunbookRecord?> GetByNameAsync(string name);
    Task<RunbookRecord?> GetByNameAndVersionAsync(string name, int version);
    Task<IEnumerable<RunbookRecord>> GetAllVersionsAsync(string name);
    Task<int> GetMaxVersionAsync(string name);
    Task<int> InsertAsync(RunbookRecord record);
    Task DeactivatePreviousVersionsAsync(string name, int currentVersion);
    Task<bool> DeactivateVersionAsync(string name, int version);
}

public class RunbookRepository : IRunbookRepository
{
    private readonly IDbConnectionFactory _db;

    public RunbookRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IEnumerable<RunbookRecord>> GetActiveRunbooksAsync()
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<RunbookRecord>(
            "SELECT * FROM runbooks WHERE is_active = 1 ORDER BY name");
    }

    public async Task<RunbookRecord?> GetByNameAsync(string name)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<RunbookRecord>(
            "SELECT * FROM runbooks WHERE name = @Name AND is_active = 1",
            new { Name = name });
    }

    public async Task<RunbookRecord?> GetByNameAndVersionAsync(string name, int version)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<RunbookRecord>(
            "SELECT * FROM runbooks WHERE name = @Name AND version = @Version",
            new { Name = name, Version = version });
    }

    public async Task<IEnumerable<RunbookRecord>> GetAllVersionsAsync(string name)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<RunbookRecord>(
            "SELECT * FROM runbooks WHERE name = @Name ORDER BY version DESC",
            new { Name = name });
    }

    public async Task<int> GetMaxVersionAsync(string name)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT ISNULL(MAX(version), 0) FROM runbooks WHERE name = @Name",
            new { Name = name });
    }

    public async Task<int> InsertAsync(RunbookRecord record)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleAsync<int>(@"
            INSERT INTO runbooks (name, version, yaml_content, data_table_name, is_active, overdue_behavior, rerun_init)
            VALUES (@Name, @Version, @YamlContent, @DataTableName, 1, @OverdueBehavior, @RerunInit);
            SELECT CAST(SCOPE_IDENTITY() AS INT);",
            record);
    }

    public async Task DeactivatePreviousVersionsAsync(string name, int currentVersion)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE runbooks SET is_active = 0 WHERE name = @Name AND version < @Version",
            new { Name = name, Version = currentVersion });
    }

    public async Task<bool> DeactivateVersionAsync(string name, int version)
    {
        using var conn = _db.CreateConnection();
        var rowsAffected = await conn.ExecuteAsync(
            "UPDATE runbooks SET is_active = 0 WHERE name = @Name AND version = @Version AND is_active = 1",
            new { Name = name, Version = version });
        return rowsAffected > 0;
    }
}
