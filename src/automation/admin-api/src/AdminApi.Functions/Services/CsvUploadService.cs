using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using MaToolkit.Automation.Shared.Models.Yaml;
using Microsoft.Extensions.Logging;

namespace AdminApi.Functions.Services;

public interface ICsvUploadService
{
    Task<CsvUploadResult> ParseCsvAsync(Stream csvStream, RunbookDefinition definition);
}

public class CsvUploadResult
{
    public bool Success { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public DataTable? Data { get; set; }
    public int RowCount => Data?.Rows.Count ?? 0;
}

public class CsvUploadService : ICsvUploadService
{
    private readonly ILogger<CsvUploadService> _logger;

    public CsvUploadService(ILogger<CsvUploadService> logger)
    {
        _logger = logger;
    }

    public async Task<CsvUploadResult> ParseCsvAsync(Stream csvStream, RunbookDefinition definition)
    {
        var result = new CsvUploadResult();

        try
        {
            using var reader = new StreamReader(csvStream);
            var content = await reader.ReadToEndAsync();
            var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (lines.Count == 0)
            {
                result.Errors.Add("CSV file is empty");
                return result;
            }

            // Parse header
            var headers = ParseCsvLine(lines[0]);
            if (headers.Count == 0)
            {
                result.Errors.Add("No columns found in CSV header");
                return result;
            }

            // Validate primary key column exists
            var primaryKey = definition.DataSource.PrimaryKey;
            var pkIndex = headers.FindIndex(h => h.Equals(primaryKey, StringComparison.OrdinalIgnoreCase));
            if (pkIndex < 0)
            {
                result.Errors.Add($"Primary key column '{primaryKey}' not found in CSV");
                return result;
            }

            // Create DataTable
            var dataTable = new DataTable();
            foreach (var header in headers)
            {
                dataTable.Columns.Add(header, typeof(string));
            }

            // Track primary keys for duplicate detection
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Parse data rows (skip header)
            for (int i = 1; i < lines.Count; i++)
            {
                var values = ParseCsvLine(lines[i]);

                // Handle rows with fewer columns (pad with empty strings)
                while (values.Count < headers.Count)
                    values.Add(string.Empty);

                // Get primary key value
                var pkValue = values[pkIndex];
                if (string.IsNullOrWhiteSpace(pkValue))
                {
                    result.Errors.Add($"Row {i + 1}: Primary key cannot be empty");
                    continue;
                }

                // Check for duplicates
                if (seenKeys.Contains(pkValue))
                {
                    result.Errors.Add($"Row {i + 1}: Duplicate primary key '{pkValue}'");
                    continue;
                }
                seenKeys.Add(pkValue);

                // Add row
                var row = dataTable.NewRow();
                for (int j = 0; j < headers.Count && j < values.Count; j++)
                {
                    row[j] = values[j];
                }
                dataTable.Rows.Add(row);
            }

            // Warn about unexpected columns (not errors, just warnings)
            var expectedColumns = GetExpectedColumns(definition);
            foreach (var header in headers)
            {
                if (!expectedColumns.Contains(header, StringComparer.OrdinalIgnoreCase))
                {
                    result.Warnings.Add($"Unexpected column '{header}' (will be included in data)");
                }
            }

            if (result.Errors.Count > 0)
            {
                return result;
            }

            result.Success = true;
            result.Data = dataTable;
            _logger.LogInformation("CSV parsed successfully: {RowCount} rows", dataTable.Rows.Count);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Failed to parse CSV: {ex.Message}");
            _logger.LogError(ex, "CSV parsing failed");
        }

        return result;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var inQuotes = false;
        var current = new System.Text.StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    // Escaped quote
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                values.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        values.Add(current.ToString().Trim());
        return values;
    }

    private static HashSet<string> GetExpectedColumns(RunbookDefinition definition)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            definition.DataSource.PrimaryKey
        };

        if (!string.IsNullOrEmpty(definition.DataSource.BatchTimeColumn))
        {
            columns.Add(definition.DataSource.BatchTimeColumn);
        }

        foreach (var mvCol in definition.DataSource.MultiValuedColumns)
        {
            columns.Add(mvCol.Name);
        }

        // Include columns referenced in templates
        var templatePattern = new Regex(@"\{\{(\w+)\}\}");
        foreach (var phase in definition.Phases)
        {
            foreach (var step in phase.Steps)
            {
                foreach (var paramValue in step.Params.Values)
                {
                    var matches = templatePattern.Matches(paramValue);
                    foreach (Match m in matches)
                    {
                        var colName = m.Groups[1].Value;
                        if (!colName.StartsWith("_"))
                            columns.Add(colName);
                    }
                }
            }
        }

        return columns;
    }
}
