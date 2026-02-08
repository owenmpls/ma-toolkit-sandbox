using System.Data;
using MaToolkit.Automation.Shared.Models.Db;

namespace MaToolkit.Automation.Shared.Services.Repositories;

public interface IInitExecutionRepository
{
    Task<InitExecutionRecord?> GetByIdAsync(int id);
    Task<InitExecutionRecord?> GetByJobIdAsync(string jobId);
    Task<IEnumerable<InitExecutionRecord>> GetByBatchAsync(int batchId);
    Task<IEnumerable<InitExecutionRecord>> GetPendingByBatchAsync(int batchId);
    Task<IEnumerable<InitExecutionRecord>> GetPollingStepsDueAsync(DateTime now);
    Task<int> InsertAsync(InitExecutionRecord record, IDbTransaction? transaction = null);
    Task<bool> SetDispatchedAsync(int id, string jobId);
    Task<bool> SetSucceededAsync(int id, string? resultJson);
    Task<bool> SetFailedAsync(int id, string errorMessage);
    Task<bool> SetPollingAsync(int id);
    Task<bool> SetPollTimeoutAsync(int id);
    Task<bool> SetRetryPendingAsync(int id, DateTime retryAfter);
    Task UpdatePollStateAsync(int id);
}
