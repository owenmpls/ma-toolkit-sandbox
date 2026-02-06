using System.Data;
using Dapper;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Services;

namespace AdminApi.Functions.Services.Repositories;

public interface IInitExecutionRepository
{
    Task<IEnumerable<InitExecutionRecord>> GetByBatchAsync(int batchId);
    Task<int> InsertAsync(InitExecutionRecord record, IDbTransaction? transaction = null);
}

public class InitExecutionRepository : IInitExecutionRepository
{
    private readonly IDbConnectionFactory _db;

    public InitExecutionRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IEnumerable<InitExecutionRecord>> GetByBatchAsync(int batchId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<InitExecutionRecord>(
            "SELECT * FROM init_executions WHERE batch_id = @BatchId ORDER BY step_index",
            new { BatchId = batchId });
    }

    public async Task<int> InsertAsync(InitExecutionRecord record, IDbTransaction? transaction = null)
    {
        var conn = transaction?.Connection ?? _db.CreateConnection();
        try
        {
            return await conn.QuerySingleAsync<int>(@"
                INSERT INTO init_executions (
                    batch_id, step_name, step_index, runbook_version,
                    worker_id, function_name, params_json, status,
                    is_poll_step, poll_interval_sec, poll_timeout_sec, on_failure)
                VALUES (
                    @BatchId, @StepName, @StepIndex, @RunbookVersion,
                    @WorkerId, @FunctionName, @ParamsJson, @Status,
                    @IsPollStep, @PollIntervalSec, @PollTimeoutSec, @OnFailure);
                SELECT CAST(SCOPE_IDENTITY() AS INT);",
                new
                {
                    record.BatchId,
                    record.StepName,
                    record.StepIndex,
                    record.RunbookVersion,
                    record.WorkerId,
                    record.FunctionName,
                    record.ParamsJson,
                    Status = StepStatus.Pending,
                    record.IsPollStep,
                    record.PollIntervalSec,
                    record.PollTimeoutSec,
                    record.OnFailure
                }, transaction);
        }
        finally
        {
            if (transaction is null && conn is IDisposable d)
                d.Dispose();
        }
    }
}
