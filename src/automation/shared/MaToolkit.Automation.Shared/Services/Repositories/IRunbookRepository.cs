using MaToolkit.Automation.Shared.Models.Db;

namespace MaToolkit.Automation.Shared.Services.Repositories;

public interface IRunbookRepository
{
    Task<IEnumerable<RunbookRecord>> GetActiveRunbooksAsync();
    Task<RunbookRecord?> GetByIdAsync(int id);
    Task<RunbookRecord?> GetByNameAsync(string name);
    Task<RunbookRecord?> GetByNameAndVersionAsync(string name, int version);
    Task<IEnumerable<RunbookRecord>> GetAllVersionsAsync(string name);
    Task<int> GetMaxVersionAsync(string name);
    Task<int> InsertAsync(RunbookRecord record);
    Task DeactivatePreviousVersionsAsync(string name, int currentVersion);
    Task<bool> DeactivateVersionAsync(string name, int version);
    Task SetIgnoreOverdueAppliedAsync(int id);
    Task SetLastErrorAsync(int id, string error);
    Task ClearLastErrorAsync(int id);
}
