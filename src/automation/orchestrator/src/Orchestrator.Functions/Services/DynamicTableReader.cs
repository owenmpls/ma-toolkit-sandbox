using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;
using MaToolkit.Automation.Shared.Services;

namespace Orchestrator.Functions.Services;

public interface IDynamicTableReader
{
    /// <summary>
    /// Load member data from a dynamic runbook table by member key.
    /// </summary>
    Task<DataRow?> GetMemberDataAsync(string tableName, string memberKey);

    /// <summary>
    /// Load member data for multiple members by their keys.
    /// </summary>
    Task<Dictionary<string, DataRow>> GetMembersDataAsync(string tableName, IEnumerable<string> memberKeys);

    /// <summary>
    /// Load member data for a batch member by their batch_member_id.
    /// </summary>
    Task<DataRow?> GetMemberDataByIdAsync(string tableName, int batchMemberId, string memberKey);
}

public class DynamicTableReader : IDynamicTableReader
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<DynamicTableReader> _logger;

    public DynamicTableReader(IDbConnectionFactory db, ILogger<DynamicTableReader> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<DataRow?> GetMemberDataAsync(string tableName, string memberKey)
    {
        using var conn = _db.CreateConnection();

        var sanitizedTableName = SanitizeTableName(tableName);
        var sql = $"SELECT * FROM [{sanitizedTableName}] WHERE _member_key = @MemberKey AND _is_current = 1";

        var results = await conn.QueryAsync(sql, new { MemberKey = memberKey });
        var resultList = results.ToList();

        if (resultList.Count == 0)
        {
            _logger.LogWarning("No member data found for key {MemberKey} in table {TableName}", memberKey, tableName);
            return null;
        }

        // Build DataRow from dynamic result
        var dataTable = new DataTable();
        var first = (IDictionary<string, object>)resultList[0];
        foreach (var key in first.Keys)
            dataTable.Columns.Add(key, first[key]?.GetType() ?? typeof(object));

        var dict = (IDictionary<string, object>)resultList[0];
        var dataRow = dataTable.NewRow();
        foreach (var key in dict.Keys)
            dataRow[key] = dict[key] ?? DBNull.Value;
        dataTable.Rows.Add(dataRow);

        return dataRow;
    }

    public async Task<Dictionary<string, DataRow>> GetMembersDataAsync(string tableName, IEnumerable<string> memberKeys)
    {
        var keyList = memberKeys.ToList();
        if (keyList.Count == 0)
            return new Dictionary<string, DataRow>();

        using var conn = _db.CreateConnection();

        var sanitizedTableName = SanitizeTableName(tableName);
        var sql = $"SELECT * FROM [{sanitizedTableName}] WHERE _member_key IN @MemberKeys AND _is_current = 1";

        // Use Dapper for IN clause handling
        var results = await conn.QueryAsync(sql, new { MemberKeys = keyList });

        var dataTable = new DataTable();
        var resultList = results.ToList();

        if (resultList.Count == 0)
            return new Dictionary<string, DataRow>();

        // Build DataTable from dynamic results
        var first = (IDictionary<string, object>)resultList[0];
        foreach (var key in first.Keys)
        {
            var value = first[key];
            dataTable.Columns.Add(key, value?.GetType() ?? typeof(object));
        }

        foreach (var row in resultList)
        {
            var dict = (IDictionary<string, object>)row;
            var dataRow = dataTable.NewRow();
            foreach (var key in dict.Keys)
            {
                dataRow[key] = dict[key] ?? DBNull.Value;
            }
            dataTable.Rows.Add(dataRow);
        }

        // Index by _member_key
        var result = new Dictionary<string, DataRow>();
        foreach (DataRow row in dataTable.Rows)
        {
            var memberKey = row["_member_key"]?.ToString();
            if (!string.IsNullOrEmpty(memberKey))
            {
                result[memberKey] = row;
            }
        }

        return result;
    }

    public async Task<DataRow?> GetMemberDataByIdAsync(string tableName, int batchMemberId, string memberKey)
    {
        // For now, we look up by member key since dynamic tables don't have batch_member_id
        return await GetMemberDataAsync(tableName, memberKey);
    }

    private static string SanitizeTableName(string tableName)
    {
        // Remove any characters that aren't alphanumeric or underscore
        return new string(tableName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
    }
}
