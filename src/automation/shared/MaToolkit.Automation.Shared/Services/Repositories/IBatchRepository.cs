using System.Data;
using MaToolkit.Automation.Shared.Models.Db;

namespace MaToolkit.Automation.Shared.Services.Repositories;

public interface IBatchRepository
{
    Task<BatchRecord?> GetByIdAsync(int id);
    Task<BatchRecord?> GetByRunbookAndTimeAsync(int runbookId, DateTime batchStartTime);
    Task<IEnumerable<BatchRecord>> GetActiveByRunbookAsync(int runbookId);
    Task<IEnumerable<BatchRecord>> ListAsync(int? runbookId = null, string? status = null, bool? isManual = null, int limit = 100);
    Task<int> InsertAsync(BatchRecord record, IDbTransaction? transaction = null);
    Task UpdateStatusAsync(int id, string status);
    Task UpdateCurrentPhaseAsync(int id, string? phaseName);
    Task SetInitDispatchedAsync(int id);
    Task SetActiveAsync(int id);
    Task<bool> SetCompletedAsync(int id);
    Task<bool> SetFailedAsync(int id);
}
