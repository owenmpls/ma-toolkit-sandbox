using System.Data;
using System.Text.Json;
using Dapper;
using MaToolkit.Automation.Shared.Models.Yaml;
using Microsoft.Extensions.Logging;

namespace MaToolkit.Automation.Shared.Services;

public class DynamicTableManager : IDynamicTableManager
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<DynamicTableManager> _logger;

    public DynamicTableManager(IDbConnectionFactory db, ILogger<DynamicTableManager> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task UpsertDataAsync(string tableName, string primaryKey, string? batchTimeColumn,
        DataTable rows, IEnumerable<MultiValuedColumnConfig> multiValuedCols)
    {
        var mvCols = multiValuedCols.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        var members = new List<MemberDataEntry>();
        foreach (DataRow row in rows.Rows)
        {
            var memberKey = row[primaryKey]?.ToString() ?? string.Empty;

            DateTime? batchTime = null;
            if (batchTimeColumn != null && rows.Columns.Contains(batchTimeColumn))
            {
                var btValue = row[batchTimeColumn]?.ToString();
                if (btValue != null)
                    batchTime = DateTime.Parse(btValue);
            }

            var data = new Dictionary<string, string>();
            foreach (DataColumn col in rows.Columns)
            {
                var value = row[col]?.ToString();
                if (mvCols.TryGetValue(col.ColumnName, out var mvConfig))
                    value = ConvertToJsonArray(value, mvConfig.Format);
                data[col.ColumnName] = value ?? string.Empty;
            }

            members.Add(new MemberDataEntry(memberKey, batchTime, JsonSerializer.Serialize(data)));
        }

        var json = JsonSerializer.Serialize(members);

        var sql = @"
            MERGE member_data AS target
            USING (
                SELECT member_key, batch_time, data_json
                FROM OPENJSON(@Json) WITH (
                    member_key NVARCHAR(256) '$.MemberKey',
                    batch_time DATETIME2 '$.BatchTime',
                    data_json NVARCHAR(MAX) '$.DataJson'
                )
            ) AS source
            ON target.runbook_table_name = @TableName AND target.member_key = source.member_key
            WHEN MATCHED THEN
                UPDATE SET data_json = source.data_json, batch_time = source.batch_time,
                           last_seen_at = SYSUTCDATETIME(), is_current = 1
            WHEN NOT MATCHED THEN
                INSERT (runbook_table_name, member_key, batch_time, data_json)
                VALUES (@TableName, source.member_key, source.batch_time, source.data_json);

            UPDATE member_data SET is_current = 0
            WHERE runbook_table_name = @TableName AND is_current = 1
              AND member_key NOT IN (
                  SELECT member_key FROM OPENJSON(@Json) WITH (member_key NVARCHAR(256) '$.MemberKey')
              );";

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(sql, new { TableName = tableName, Json = json });

        _logger.LogInformation("Upserted {Count} members into member_data for {TableName}", members.Count, tableName);
    }

    private static string? ConvertToJsonArray(string? value, string format)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        if (string.Equals(format, "json_array", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(value);
                return value;
            }
            catch (JsonException)
            {
                throw new ArgumentException($"Value is not valid JSON for json_array column: {value}");
            }
        }

        string[] parts = format.ToLowerInvariant() switch
        {
            "semicolon_delimited" => value.Split(';', StringSplitOptions.RemoveEmptyEntries),
            "comma_delimited" => value.Split(',', StringSplitOptions.RemoveEmptyEntries),
            _ => value.Split(';', StringSplitOptions.RemoveEmptyEntries)
        };

        return JsonSerializer.Serialize(parts.Select(p => p.Trim()).ToArray());
    }

    private record MemberDataEntry(string MemberKey, DateTime? BatchTime, string DataJson);
}
