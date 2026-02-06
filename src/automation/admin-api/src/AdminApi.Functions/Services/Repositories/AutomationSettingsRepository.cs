using Dapper;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Services;

namespace AdminApi.Functions.Services.Repositories;

public interface IAutomationSettingsRepository
{
    Task<AutomationSettingsRecord?> GetByNameAsync(string runbookName);
    Task<IEnumerable<AutomationSettingsRecord>> GetAllAsync();
    Task UpsertAsync(AutomationSettingsRecord record);
}

public class AutomationSettingsRepository : IAutomationSettingsRepository
{
    private readonly IDbConnectionFactory _db;

    public AutomationSettingsRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<AutomationSettingsRecord?> GetByNameAsync(string runbookName)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<AutomationSettingsRecord>(
            "SELECT * FROM runbook_automation_settings WHERE runbook_name = @RunbookName",
            new { RunbookName = runbookName });
    }

    public async Task<IEnumerable<AutomationSettingsRecord>> GetAllAsync()
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<AutomationSettingsRecord>(
            "SELECT * FROM runbook_automation_settings ORDER BY runbook_name");
    }

    public async Task UpsertAsync(AutomationSettingsRecord record)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            MERGE runbook_automation_settings AS target
            USING (SELECT @RunbookName AS runbook_name) AS source
            ON target.runbook_name = source.runbook_name
            WHEN MATCHED THEN
                UPDATE SET
                    automation_enabled = @AutomationEnabled,
                    enabled_at = @EnabledAt,
                    enabled_by = @EnabledBy,
                    disabled_at = @DisabledAt,
                    disabled_by = @DisabledBy
            WHEN NOT MATCHED THEN
                INSERT (runbook_name, automation_enabled, enabled_at, enabled_by, disabled_at, disabled_by)
                VALUES (@RunbookName, @AutomationEnabled, @EnabledAt, @EnabledBy, @DisabledAt, @DisabledBy);",
            record);
    }
}
