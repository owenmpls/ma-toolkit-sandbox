using System.Data;
using MaToolkit.Automation.Shared.Models.Db;

namespace MaToolkit.Automation.Shared.Services.Repositories;

public interface IPhaseExecutionRepository
{
    Task<PhaseExecutionRecord?> GetByIdAsync(int id);
    Task<IEnumerable<PhaseExecutionRecord>> GetByBatchAsync(int batchId);
    Task<IEnumerable<PhaseExecutionRecord>> GetDispatchedByBatchAsync(int batchId);
    Task<PhaseExecutionRecord?> GetFirstPendingAsync(int batchId);
    Task<IEnumerable<PhaseExecutionRecord>> GetPendingDueAsync(int batchId, DateTime now);
    Task<int> InsertAsync(PhaseExecutionRecord record, IDbTransaction? transaction = null);
    Task<bool> SetDispatchedAsync(int id);
    Task UpdateStatusAsync(int id, string status);
    Task<bool> SetCompletedAsync(int id);
    Task<bool> SetFailedAsync(int id);
    Task<int> SupersedeOldVersionPendingAsync(int batchId, int currentVersion);
}
