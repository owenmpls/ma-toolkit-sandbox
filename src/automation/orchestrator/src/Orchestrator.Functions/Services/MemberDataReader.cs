using System.Text.Json;
using Dapper;
using MaToolkit.Automation.Shared.Services;
using Microsoft.Extensions.Logging;

namespace Orchestrator.Functions.Services;

public class MemberDataReader : IMemberDataReader
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<MemberDataReader> _logger;

    public MemberDataReader(IDbConnectionFactory db, ILogger<MemberDataReader> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Dictionary<string, string>?> GetMemberDataAsync(string tableName, string memberKey)
    {
        using var conn = _db.CreateConnection();
        var json = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT data_json FROM member_data WHERE runbook_table_name = @TableName AND member_key = @MemberKey AND is_current = 1",
            new { TableName = tableName, MemberKey = memberKey });

        if (json == null)
        {
            _logger.LogWarning("No member data found for key {MemberKey} in table {TableName}", memberKey, tableName);
            return null;
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
    }

    public async Task<Dictionary<string, Dictionary<string, string>>> GetMembersDataAsync(
        string tableName, IEnumerable<string> memberKeys)
    {
        var keyList = memberKeys.ToList();
        if (keyList.Count == 0)
            return new Dictionary<string, Dictionary<string, string>>();

        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<(string member_key, string data_json)>(
            "SELECT member_key, data_json FROM member_data WHERE runbook_table_name = @TableName AND member_key IN @MemberKeys AND is_current = 1",
            new { TableName = tableName, MemberKeys = keyList });

        return rows.ToDictionary(
            r => r.member_key,
            r => JsonSerializer.Deserialize<Dictionary<string, string>>(r.data_json)!);
    }
}
