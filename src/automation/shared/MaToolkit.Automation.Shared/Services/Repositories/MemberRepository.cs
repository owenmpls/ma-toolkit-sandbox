using System.Data;
using System.Text.Json;
using Dapper;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;

namespace MaToolkit.Automation.Shared.Services.Repositories;

public class MemberRepository : IMemberRepository
{
    private readonly IDbConnectionFactory _db;

    public MemberRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<BatchMemberRecord?> GetByIdAsync(int id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<BatchMemberRecord>(
            "SELECT * FROM batch_members WHERE id = @Id",
            new { Id = id });
    }

    public async Task<IEnumerable<BatchMemberRecord>> GetByBatchAsync(int batchId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<BatchMemberRecord>(
            "SELECT * FROM batch_members WHERE batch_id = @BatchId ORDER BY id",
            new { BatchId = batchId });
    }

    public async Task<IEnumerable<BatchMemberRecord>> GetActiveByBatchAsync(int batchId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<BatchMemberRecord>(
            "SELECT * FROM batch_members WHERE batch_id = @BatchId AND status = @Status ORDER BY id",
            new { BatchId = batchId, Status = MemberStatus.Active });
    }

    public async Task<int> InsertAsync(BatchMemberRecord record, IDbTransaction? transaction = null)
    {
        var conn = transaction?.Connection ?? _db.CreateConnection();
        try
        {
            return await conn.QuerySingleAsync<int>(@"
                INSERT INTO batch_members (batch_id, member_key, data_json, status)
                VALUES (@BatchId, @MemberKey, @DataJson, @Status);
                SELECT CAST(SCOPE_IDENTITY() AS INT);",
                new { record.BatchId, record.MemberKey, record.DataJson, Status = MemberStatus.Active }, transaction);
        }
        finally
        {
            if (transaction is null && conn is IDisposable d)
                d.Dispose();
        }
    }

    public async Task MarkRemovedAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE batch_members SET status = @Status, removed_at = SYSUTCDATETIME() WHERE id = @Id",
            new { Id = id, Status = MemberStatus.Removed });
    }

    public async Task SetAddDispatchedAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE batch_members SET add_dispatched_at = SYSUTCDATETIME() WHERE id = @Id",
            new { Id = id });
    }

    public async Task SetRemoveDispatchedAsync(int id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE batch_members SET remove_dispatched_at = SYSUTCDATETIME() WHERE id = @Id",
            new { Id = id });
    }

    public async Task<bool> SetFailedAsync(int id)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(
            @"UPDATE batch_members SET status = @Status, failed_at = SYSUTCDATETIME()
              WHERE id = @Id AND status = @ExpectedStatus",
            new { Id = id, Status = MemberStatus.Failed, ExpectedStatus = MemberStatus.Active });
        return rows > 0;
    }

    public async Task UpdateDataJsonAsync(int id, string dataJson)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE batch_members SET data_json = @DataJson WHERE id = @Id AND status = @Status",
            new { Id = id, DataJson = dataJson, Status = MemberStatus.Active });
    }

    public async Task MergeWorkerDataAsync(int id, Dictionary<string, string> outputData)
    {
        using var conn = _db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT worker_data_json FROM batch_members WHERE id = @Id",
            new { Id = id });

        var merged = string.IsNullOrEmpty(existing)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(existing) ?? new();

        foreach (var (key, value) in outputData)
            merged[key] = value;

        await conn.ExecuteAsync(
            "UPDATE batch_members SET worker_data_json = @WorkerDataJson WHERE id = @Id",
            new { Id = id, WorkerDataJson = JsonSerializer.Serialize(merged) });
    }

    public async Task<bool> IsMemberInActiveBatchAsync(int runbookId, string memberKey)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<bool>(@"
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM batch_members bm
                JOIN batches b ON bm.batch_id = b.id
                WHERE b.runbook_id = @RunbookId
                  AND bm.member_key = @MemberKey
                  AND bm.status = @MemberActive
                  AND b.status NOT IN (@BatchCompleted, @BatchFailed)
            ) THEN 1 ELSE 0 END",
            new
            {
                RunbookId = runbookId,
                MemberKey = memberKey,
                MemberActive = MemberStatus.Active,
                BatchCompleted = BatchStatus.Completed,
                BatchFailed = BatchStatus.Failed
            });
    }
}
