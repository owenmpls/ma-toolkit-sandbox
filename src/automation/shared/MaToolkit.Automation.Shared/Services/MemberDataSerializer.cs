using System.Data;
using System.Text.Json;
using MaToolkit.Automation.Shared.Models.Yaml;

namespace MaToolkit.Automation.Shared.Services;

public static class MemberDataSerializer
{
    public static string Serialize(DataRow row, IReadOnlyDictionary<string, MultiValuedColumnConfig>? mvCols = null)
    {
        var data = new Dictionary<string, string>();
        foreach (DataColumn col in row.Table.Columns)
        {
            var value = row[col]?.ToString();
            if (mvCols != null && mvCols.TryGetValue(col.ColumnName, out var mvConfig))
                value = ConvertToJsonArray(value, mvConfig.Format);
            data[col.ColumnName] = value ?? string.Empty;
        }
        return JsonSerializer.Serialize(data);
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
}
