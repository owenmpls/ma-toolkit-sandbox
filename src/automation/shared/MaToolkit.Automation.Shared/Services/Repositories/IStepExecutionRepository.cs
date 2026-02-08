using System.Data;
using MaToolkit.Automation.Shared.Models.Db;

namespace MaToolkit.Automation.Shared.Services.Repositories;

public interface IStepExecutionRepository
{
    Task<StepExecutionRecord?> GetByIdAsync(int id);
    Task<StepExecutionRecord?> GetByJobIdAsync(string jobId);
    Task<IEnumerable<StepExecutionRecord>> GetByBatchAsync(int batchId);
    Task<IEnumerable<StepExecutionRecord>> GetByPhaseExecutionAsync(int phaseExecutionId);
    Task<IEnumerable<StepExecutionRecord>> GetPendingByPhaseAndIndexAsync(int phaseExecutionId, int stepIndex);
    Task<IEnumerable<StepExecutionRecord>> GetPendingByMemberAsync(int batchMemberId);
    Task<IEnumerable<StepExecutionRecord>> GetByPhaseAndMemberAsync(int phaseExecutionId, int batchMemberId);
    Task<IEnumerable<StepExecutionRecord>> GetPollingStepsDueAsync(DateTime now);
    Task<int> InsertAsync(StepExecutionRecord record, IDbTransaction? transaction = null);
    Task<bool> SetDispatchedAsync(int id, string jobId);
    Task<bool> SetSucceededAsync(int id, string? resultJson);
    Task<bool> SetFailedAsync(int id, string errorMessage);
    Task<bool> SetPollingAsync(int id);
    Task<bool> SetPollTimeoutAsync(int id);
    Task<bool> SetCancelledAsync(int id);
    Task<bool> SetRetryPendingAsync(int id, DateTime retryAfter);
    Task UpdatePollStateAsync(int id);
}
