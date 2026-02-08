using System.Data;
using MaToolkit.Automation.Shared.Models.Db;

namespace MaToolkit.Automation.Shared.Services.Repositories;

public interface IMemberRepository
{
    Task<BatchMemberRecord?> GetByIdAsync(int id);
    Task<IEnumerable<BatchMemberRecord>> GetByBatchAsync(int batchId);
    Task<IEnumerable<BatchMemberRecord>> GetActiveByBatchAsync(int batchId);
    Task<int> InsertAsync(BatchMemberRecord record, IDbTransaction? transaction = null);
    Task MarkRemovedAsync(int id);
    Task SetAddDispatchedAsync(int id);
    Task SetRemoveDispatchedAsync(int id);
    Task<bool> SetFailedAsync(int id);
    Task UpdateDataJsonAsync(int id, string dataJson);
    Task MergeWorkerDataAsync(int id, Dictionary<string, string> outputData);
    Task<bool> IsMemberInActiveBatchAsync(int runbookId, string memberKey);
}
