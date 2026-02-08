using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MaToolkit.Automation.Shared.Models.Yaml;
using Microsoft.Extensions.Logging;

namespace MaToolkit.Automation.Shared.Services;

public class DynamicTableManager : IDynamicTableManager
{
    private static readonly Regex ValidColumnName = new(@"^[a-zA-Z0-9_]+$", RegexOptions.Compiled);
    private static readonly HashSet<string> SqlReservedKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD", "ALTER", "AND", "AS", "ASC", "BACKUP", "BEGIN", "BETWEEN", "BREAK",
        "BY", "CASCADE", "CASE", "CHECK", "CLOSE", "COLUMN", "COMMIT", "CONSTRAINT",
        "CREATE", "CROSS", "CURSOR", "DATABASE", "DECLARE", "DEFAULT", "DELETE",
        "DESC", "DISTINCT", "DROP", "ELSE", "END", "EXEC", "EXECUTE", "EXISTS",
        "FETCH", "FOR", "FOREIGN", "FROM", "FULL", "FUNCTION", "GOTO", "GRANT",
        "GROUP", "HAVING", "IDENTITY", "IF", "IN", "INDEX", "INNER", "INSERT",
        "INTO", "IS", "JOIN", "KEY", "LEFT", "LIKE", "NOT", "NULL", "OF", "OFF",
        "ON", "OPEN", "OR", "ORDER", "OUTER", "OVER", "PRIMARY", "PRINT", "PROC",
        "PROCEDURE", "RAISERROR", "REFERENCES", "RETURN", "REVOKE", "RIGHT",
        "ROLLBACK", "SCHEMA", "SELECT", "SET", "TABLE", "THEN", "TOP", "TRAN",
        "TRANSACTION", "TRIGGER", "TRUNCATE", "UNION", "UNIQUE", "UPDATE", "USE",
        "VALUES", "VIEW", "WHEN", "WHERE", "WHILE"
    };
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<DynamicTableManager> _logger;

    public DynamicTableManager(IDbConnectionFactory db, ILogger<DynamicTableManager> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task EnsureTableAsync(string tableName, IEnumerable<string> queryColumns, IEnumerable<MultiValuedColumnConfig> multiValuedCols)
    {
        ValidateTableName(tableName);

        using var conn = _db.CreateConnection();

        var exists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @TableName",
            new { TableName = tableName });

        if (exists > 0)
        {
            _logger.LogDebug("Dynamic table {TableName} already exists", tableName);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE [{tableName}] (");
        sb.AppendLine("    _row_id INT IDENTITY(1,1) PRIMARY KEY,");
        sb.AppendLine("    _member_key NVARCHAR(256) NOT NULL,");
        sb.AppendLine("    _batch_time DATETIME2,");
        sb.AppendLine("    _first_seen_at DATETIME2 DEFAULT SYSUTCDATETIME(),");
        sb.AppendLine("    _last_seen_at DATETIME2 DEFAULT SYSUTCDATETIME(),");
        sb.AppendLine("    _is_current BIT DEFAULT 1,");

        foreach (var col in queryColumns)
        {
            ValidateColumnName(col);
            sb.AppendLine($"    [{col}] NVARCHAR(MAX),");
        }

        sb.AppendLine($"    CONSTRAINT [UQ_{tableName}_member] UNIQUE (_member_key)");
        sb.AppendLine(");");

        await conn.ExecuteAsync(sb.ToString());
        _logger.LogInformation("Created dynamic table {TableName}", tableName);
    }

    public async Task UpsertDataAsync(string tableName, string primaryKey, string? batchTimeColumn,
        DataTable rows, IEnumerable<MultiValuedColumnConfig> multiValuedCols)
    {
        ValidateTableName(tableName);
        var mvCols = multiValuedCols.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        using var conn = _db.CreateConnection();
        conn.Open();

        var columns = rows.Columns.Cast<DataColumn>()
            .Select(c => c.ColumnName)
            .Where(c => !string.Equals(c, primaryKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var col in columns)
            ValidateColumnName(col);
        ValidateColumnName(primaryKey);

        var currentKeys = new HashSet<string>();

        foreach (DataRow row in rows.Rows)
        {
            var memberKey = row[primaryKey]?.ToString() ?? string.Empty;
            currentKeys.Add(memberKey);

            var batchTime = batchTimeColumn != null && rows.Columns.Contains(batchTimeColumn)
                ? row[batchTimeColumn]?.ToString()
                : null;

            var parameters = new DynamicParameters();
            parameters.Add("@_member_key", memberKey);
            parameters.Add("@_batch_time", batchTime != null ? DateTime.Parse(batchTime) : (DateTime?)null);

            var setClauses = new List<string> { "_last_seen_at = SYSUTCDATETIME()", "_is_current = 1" };
            var insertCols = new List<string> { "_member_key", "_batch_time" };
            var insertVals = new List<string> { "@_member_key", "@_batch_time" };

            foreach (var col in columns)
            {
                var value = row[col]?.ToString();

                if (mvCols.TryGetValue(col, out var mvConfig))
                    value = ConvertToJsonArray(value, mvConfig.Format);

                var paramName = $"@p_{col}";
                parameters.Add(paramName, value);
                setClauses.Add($"[{col}] = {paramName}");
                insertCols.Add($"[{col}]");
                insertVals.Add(paramName);
            }

            var sql = $@"
                MERGE [{tableName}] AS target
                USING (SELECT @_member_key AS _member_key) AS source
                ON target._member_key = source._member_key
                WHEN MATCHED THEN
                    UPDATE SET {string.Join(", ", setClauses)}
                WHEN NOT MATCHED THEN
                    INSERT ({string.Join(", ", insertCols)})
                    VALUES ({string.Join(", ", insertVals)});";

            await conn.ExecuteAsync(sql, parameters);
        }

        // Mark rows no longer in results
        if (currentKeys.Count > 0)
        {
            await conn.ExecuteAsync(
                $"UPDATE [{tableName}] SET _is_current = 0 WHERE _member_key NOT IN @Keys AND _is_current = 1",
                new { Keys = currentKeys.ToArray() });
        }
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

    private static void ValidateTableName(string name)
    {
        if (!ValidColumnName.IsMatch(name))
            throw new ArgumentException($"Invalid table name: {name}");
        if (SqlReservedKeywords.Contains(name))
            throw new ArgumentException($"Table name is a SQL reserved keyword: {name}");
    }

    private static void ValidateColumnName(string name)
    {
        if (!ValidColumnName.IsMatch(name))
            throw new ArgumentException($"Invalid column name: {name}");
        if (SqlReservedKeywords.Contains(name))
            throw new ArgumentException($"Column name is a SQL reserved keyword: {name}");
    }
}
