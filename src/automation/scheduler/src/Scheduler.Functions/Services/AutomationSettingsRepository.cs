using Dapper;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Services;

namespace Scheduler.Functions.Services;

public interface IAutomationSettingsRepository
{
    Task<AutomationSettingsRecord?> GetByNameAsync(string runbookName);
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
}
