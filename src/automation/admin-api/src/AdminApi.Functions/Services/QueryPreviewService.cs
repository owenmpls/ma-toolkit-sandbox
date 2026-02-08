using System.Data;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using AdminApi.Functions.Models.Responses;
using Microsoft.Extensions.Logging;

namespace AdminApi.Functions.Services;

public interface IQueryPreviewService
{
    Task<QueryPreviewResponse> ExecutePreviewAsync(RunbookDefinition definition);
}

public class QueryPreviewService : IQueryPreviewService
{
    private readonly IDataSourceQueryService _dataSourceQuery;
    private readonly ILogger<QueryPreviewService> _logger;
    private const int MaxSampleRows = 100;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);

    public QueryPreviewService(
        IDataSourceQueryService dataSourceQuery,
        ILogger<QueryPreviewService> logger)
    {
        _dataSourceQuery = dataSourceQuery;
        _logger = logger;
    }

    public async Task<QueryPreviewResponse> ExecutePreviewAsync(RunbookDefinition definition)
    {
        _logger.LogInformation("Executing query preview for runbook {RunbookName}", definition.Name);

        using var cts = new CancellationTokenSource(QueryTimeout);
        DataTable results;
        try
        {
            results = await _dataSourceQuery.ExecuteAsync(definition.DataSource).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Query preview timed out after {QueryTimeout.TotalSeconds} seconds");
        }

        var response = new QueryPreviewResponse
        {
            RowCount = results.Rows.Count,
            Columns = results.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList()
        };

        // Add sample rows (up to MaxSampleRows)
        var sampleCount = Math.Min(results.Rows.Count, MaxSampleRows);
        for (int i = 0; i < sampleCount; i++)
        {
            var row = results.Rows[i];
            var rowDict = new Dictionary<string, object?>();
            foreach (DataColumn col in results.Columns)
            {
                var value = row[col];
                rowDict[col.ColumnName] = value == DBNull.Value ? null : value;
            }
            response.Sample.Add(rowDict);
        }

        // Calculate batch groups
        var batchGroups = GroupByBatchTime(results, definition.DataSource);
        response.BatchGroups = batchGroups
            .Select(g => new BatchGroup
            {
                BatchTime = g.Key.ToString("o"),
                MemberCount = g.Value
            })
            .OrderBy(g => g.BatchTime)
            .ToList();

        _logger.LogInformation(
            "Query preview complete: {RowCount} rows, {BatchCount} batch groups",
            response.RowCount, response.BatchGroups.Count);

        return response;
    }

    private Dictionary<DateTime, int> GroupByBatchTime(DataTable results, DataSourceConfig config)
    {
        var groups = new Dictionary<DateTime, int>();
        bool isImmediate = string.Equals(config.BatchTime, "immediate", StringComparison.OrdinalIgnoreCase);

        foreach (DataRow row in results.Rows)
        {
            DateTime batchTime;

            if (isImmediate)
            {
                // For immediate runbooks, use 5-minute rounding (matches scheduler logic)
                var now = DateTime.UtcNow;
                batchTime = new DateTime(now.Year, now.Month, now.Day, now.Hour,
                    (now.Minute / 5) * 5, 0, DateTimeKind.Utc);
            }
            else
            {
                var timeValue = row[config.BatchTimeColumn!]?.ToString();
                if (string.IsNullOrEmpty(timeValue) || !DateTime.TryParse(timeValue, out batchTime))
                {
                    continue; // Skip rows with invalid batch time
                }
            }

            if (groups.ContainsKey(batchTime))
                groups[batchTime]++;
            else
                groups[batchTime] = 1;
        }

        return groups;
    }
}
