using MaToolkit.Automation.Shared.Models.Db;

namespace MaToolkit.Automation.Shared.Services.Repositories;

public interface IAutomationSettingsRepository
{
    Task<AutomationSettingsRecord?> GetByNameAsync(string runbookName);
    Task<IEnumerable<AutomationSettingsRecord>> GetAllAsync();
    Task UpsertAsync(AutomationSettingsRecord record);
}
