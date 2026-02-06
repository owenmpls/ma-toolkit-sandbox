using System.Data;
using Dapper;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Services;

namespace AdminApi.Functions.Services.Repositories;

public interface IBatchRepository
{
    Task<BatchRecord?> GetByIdAsync(int id);
    Task<IEnumerable<BatchRecord>> ListAsync(int? runbookId = null, string? status = null, bool? isManual = null, int limit = 100);
    Task<int> InsertAsync(BatchRecord record, IDbTransaction? transaction = null);
    Task UpdateStatusAsync(int id, string status);
    Task UpdateCurrentPhaseAsync(int id, string? phaseName);
    Task SetInitDispatchedAsync(int id);
}

public class BatchRepository : IBatchRepository
{
    private readonly IDbConnectionFactory _db;

    public BatchRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<BatchRecord?> GetByIdAsync(int id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<BatchRecord>(
            "SELECT * FROM batches WHERE id = @Id",
            new { Id = id });
    }

    public async Task<IEnumerable<BatchRecord>> ListAsync(int? runbookId = null, string? status = null, bool? isManual = null, int limit = 100)
    {
        using var conn = _db.CreateConnection();

        var sql = "SELECT TOP (@Limit) * FROM batches WHERE 1=1";
        var parameters = new DynamicParameters();
        parameters.Add("@Limit", limit);

        if (runbookId.HasValue)
        {
            sql += " AND runbook_id = @RunbookId";
            parameters.Add("@RunbookId", runbookId.Value);
        }

        if (!string.IsNullOrEmpty(status))
        {
            sql += " AND status = @Status";
            parameters.Add("@Status", status);
        }

        if (isManual.HasValue)
        {
            sql += " AND is_manual = @IsManual";
            parameters.Add("@IsManual", isManual.Value);
        }

        sql += " ORDER BY detected_at DESC";

        return await conn.QueryAsync<BatchRecord>(sql, parameters);
    }

    public async Task<int> InsertAsync(BatchRecord record, IDbTransaction? transaction = null)
    {
        var conn = transaction?.Connection ?? _db.CreateConnection();
        try
        {
            return await conn.QuerySingleAsync<int>(@"
                INSERT INTO batches (runbook_id, batch_start_time, status, is_manual, created_by, current_phase)
                VALUES (@RunbookId, @BatchStartTime, @Status, @IsManual, @CreatedBy, @CurrentPhase);
                SELECT CAST(SCOPE_IDENTITY() AS INT);",
                record, transaction);
        }
        finally
        {
            if (transaction is null && conn is IDisposable d)
                d.Dispose();
        }
    }

    public async Task UpdateStatusAsync(int id, string status)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE batches SET status = @Status WHERE id = @Id",
            new { Id = id, Status = status });
    }

    public async Task UpdateCurrentPhaseAsync(int id, string? phaseName)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE batches SET current_phase = @PhaseName WHERE id = @Id",
            new { Id = id, PhaseName = phaseName });
    }

    public async Task SetInitDispatchedAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            @"UPDATE batches SET status = @Status, init_dispatched_at = SYSUTCDATETIME() WHERE id = @Id",
            new { Id = id, Status = BatchStatus.InitDispatched });
    }
}
