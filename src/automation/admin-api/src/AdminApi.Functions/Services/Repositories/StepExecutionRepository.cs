using Dapper;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Services;

namespace AdminApi.Functions.Services.Repositories;

public interface IStepExecutionRepository
{
    Task<IEnumerable<StepExecutionRecord>> GetByBatchAsync(int batchId);
    Task<IEnumerable<StepExecutionRecord>> GetByPhaseExecutionAsync(int phaseExecutionId);
}

public class StepExecutionRepository : IStepExecutionRepository
{
    private readonly IDbConnectionFactory _db;

    public StepExecutionRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IEnumerable<StepExecutionRecord>> GetByBatchAsync(int batchId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<StepExecutionRecord>(@"
            SELECT se.* FROM step_executions se
            JOIN phase_executions pe ON se.phase_execution_id = pe.id
            WHERE pe.batch_id = @BatchId
            ORDER BY pe.offset_minutes, se.step_index",
            new { BatchId = batchId });
    }

    public async Task<IEnumerable<StepExecutionRecord>> GetByPhaseExecutionAsync(int phaseExecutionId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<StepExecutionRecord>(
            "SELECT * FROM step_executions WHERE phase_execution_id = @PhaseExecutionId ORDER BY step_index",
            new { PhaseExecutionId = phaseExecutionId });
    }
}
