using System.Text;
using System.Text.RegularExpressions;
using MaToolkit.Automation.Shared.Models.Yaml;
using Microsoft.Extensions.Logging;

namespace AdminApi.Functions.Services;

public interface ICsvTemplateService
{
    string GenerateTemplate(RunbookDefinition definition);
}

public class CsvTemplateService : ICsvTemplateService
{
    private readonly ILogger<CsvTemplateService> _logger;
    private static readonly Regex SelectColumnsPattern = new(
        @"SELECT\s+(.*?)\s+FROM",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public CsvTemplateService(ILogger<CsvTemplateService> logger)
    {
        _logger = logger;
    }

    public string GenerateTemplate(RunbookDefinition definition)
    {
        _logger.LogInformation("Generating CSV template for runbook {RunbookName}", definition.Name);

        var columns = ExtractColumns(definition);
        var multiValuedCols = definition.DataSource.MultiValuedColumns
            .ToDictionary(c => c.Name, c => c.Format, StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();

        // Header row
        sb.AppendLine(string.Join(",", columns.Select(EscapeCsvField)));

        // Sample row with format hints
        var sampleValues = columns.Select(col => GetSampleValue(col, multiValuedCols));
        sb.AppendLine(string.Join(",", sampleValues.Select(EscapeCsvField)));

        return sb.ToString();
    }

    private List<string> ExtractColumns(RunbookDefinition definition)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Always include primary key
        columns.Add(definition.DataSource.PrimaryKey);

        // Include batch time column if not immediate
        if (!string.Equals(definition.DataSource.BatchTime, "immediate", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(definition.DataSource.BatchTimeColumn))
        {
            columns.Add(definition.DataSource.BatchTimeColumn);
        }

        // Try to parse SELECT clause from query
        var match = SelectColumnsPattern.Match(definition.DataSource.Query);
        if (match.Success)
        {
            var selectClause = match.Groups[1].Value;

            // Handle SELECT * case
            if (selectClause.Trim() == "*")
            {
                _logger.LogWarning("Query uses SELECT *, cannot extract column names automatically");
            }
            else
            {
                // Parse column list (handle aliases like "column AS alias" and "table.column")
                var columnParts = selectClause.Split(',');
                foreach (var part in columnParts)
                {
                    var trimmed = part.Trim();

                    // Handle AS alias
                    var asIndex = trimmed.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
                    if (asIndex >= 0)
                    {
                        trimmed = trimmed.Substring(asIndex + 4).Trim();
                    }
                    else
                    {
                        // Handle table.column - take just the column name
                        var dotIndex = trimmed.LastIndexOf('.');
                        if (dotIndex >= 0)
                        {
                            trimmed = trimmed.Substring(dotIndex + 1);
                        }
                    }

                    // Remove brackets and quotes
                    trimmed = trimmed.Trim('[', ']', '"', '\'', '`');

                    if (!string.IsNullOrEmpty(trimmed) && !trimmed.Contains(' '))
                    {
                        columns.Add(trimmed);
                    }
                }
            }
        }

        // Collect output_params keys — these are populated at runtime, not from CSV
        var outputParamKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var phase in definition.Phases)
        {
            foreach (var step in phase.Steps)
            {
                foreach (var key in step.OutputParams.Keys)
                    outputParamKeys.Add(key);
            }
        }

        // Include columns referenced in step params ({{ColumnName}} templates)
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
                        // Exclude special variables and output_params (runtime-populated)
                        if (!colName.StartsWith("_") && !outputParamKeys.Contains(colName))
                        {
                            columns.Add(colName);
                        }
                    }
                }
            }
        }

        // Include multi-valued columns
        foreach (var mvCol in definition.DataSource.MultiValuedColumns)
        {
            columns.Add(mvCol.Name);
        }

        return columns.ToList();
    }

    private static string GetSampleValue(string column, Dictionary<string, string> multiValuedCols)
    {
        if (multiValuedCols.TryGetValue(column, out var format))
        {
            return format.ToLowerInvariant() switch
            {
                "semicolon_delimited" => "value1;value2;value3",
                "comma_delimited" => "value1,value2,value3",
                "json_array" => "[\"value1\",\"value2\",\"value3\"]",
                _ => "value1;value2;value3"
            };
        }

        // Check if it looks like a date/time column
        if (column.Contains("time", StringComparison.OrdinalIgnoreCase)
            || column.Contains("date", StringComparison.OrdinalIgnoreCase))
        {
            return DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-ddTHH:mm:ssZ");
        }

        // Check if it looks like an email
        if (column.Contains("email", StringComparison.OrdinalIgnoreCase))
        {
            return "user@example.com";
        }

        // Check if it looks like an ID
        if (column.EndsWith("_id", StringComparison.OrdinalIgnoreCase)
            || column.EndsWith("Id", StringComparison.Ordinal))
        {
            return "sample_id_001";
        }

        return "sample_value";
    }

    private static string EscapeCsvField(string field)
    {
        if (string.IsNullOrEmpty(field))
            return "";

        // Escape if contains comma, quote, or newline
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        return field;
    }
}
