using System.Data;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MaToolkit.Automation.Shared.Settings;

namespace MaToolkit.Automation.Shared.Services;

public class DatabricksQueryClient : IDatabricksQueryClient
{
    // Microsoft's well-known Entra ID app registration for Azure Databricks
    private const string DatabricksEntraIdAppId = "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d";

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<DatabricksQueryClient> _logger;
    private readonly QueryClientSettings _settings;

    public DatabricksQueryClient(HttpClient httpClient, IConfiguration config, ILogger<DatabricksQueryClient> logger, IOptions<QueryClientSettings> settings)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
        _settings = settings.Value;
    }

    public async Task<DataTable> ExecuteQueryAsync(string connectionEnvVar, string warehouseIdEnvVar, string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionEnvVar);
        ArgumentException.ThrowIfNullOrWhiteSpace(warehouseIdEnvVar);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var workspaceUrl = _config[connectionEnvVar]
            ?? throw new InvalidOperationException($"Configuration '{connectionEnvVar}' not found");
        var warehouseId = _config[warehouseIdEnvVar]
            ?? throw new InvalidOperationException($"Configuration '{warehouseIdEnvVar}' not found");

        _logger.LogInformation("Executing Databricks SQL query on warehouse {WarehouseId}", warehouseId);

        var credential = new DefaultAzureCredential();
        var token = await credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(new[] { $"{DatabricksEntraIdAppId}/.default" }));

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        var requestBody = new
        {
            warehouse_id = warehouseId,
            statement = query,
            wait_timeout = $"{_settings.DatabricksWaitTimeoutSeconds}s",
            disposition = "INLINE"
        };

        using var response = await _httpClient.PostAsync(
            $"{workspaceUrl.TrimEnd('/')}/api/2.0/sql/statements",
            new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"));

        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync();
        var result = JsonDocument.Parse(responseJson);

        var status = result.RootElement.GetProperty("status").GetProperty("state").GetString();

        // Poll if pending
        if (status == "PENDING" || status == "RUNNING")
        {
            var statementId = result.RootElement.GetProperty("statement_id").GetString();
            result.Dispose();
            result = await PollForCompletionAsync(workspaceUrl, statementId!);
        }

        try
        {
            return BuildDataTable(result);
        }
        finally
        {
            result.Dispose();
        }
    }

    private async Task<JsonDocument> PollForCompletionAsync(string workspaceUrl, string statementId)
    {
        var maxDuration = TimeSpan.FromMinutes(_settings.DatabricksPollingTimeoutMinutes);
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < maxDuration)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));

            using var response = await _httpClient.GetAsync(
                $"{workspaceUrl.TrimEnd('/')}/api/2.0/sql/statements/{statementId}");
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(responseJson);

            var status = result.RootElement.GetProperty("status").GetProperty("state").GetString();

            switch (status)
            {
                case "SUCCEEDED":
                    return result;
                case "FAILED":
                case "CANCELED":
                case "CLOSED":
                    var error = result.RootElement.GetProperty("status")
                        .TryGetProperty("error", out var errProp) ? errProp.ToString() : "Unknown error";
                    result.Dispose();
                    throw new InvalidOperationException($"Databricks query {status}: {error}");
                default:
                    result.Dispose();
                    _logger.LogDebug("Databricks statement {StatementId} status: {Status}", statementId, status);
                    continue;
            }
        }

        throw new TimeoutException($"Databricks statement {statementId} did not complete within {maxDuration.TotalMinutes} minutes");
    }

    private static DataTable BuildDataTable(JsonDocument result)
    {
        var dataTable = new DataTable();
        var root = result.RootElement;

        var columns = root.GetProperty("manifest").GetProperty("schema").GetProperty("columns");
        foreach (var col in columns.EnumerateArray())
        {
            var colName = col.GetProperty("name").GetString()!;
            dataTable.Columns.Add(colName, typeof(string));
        }

        if (root.TryGetProperty("result", out var resultProp) &&
            resultProp.TryGetProperty("data_array", out var dataArray))
        {
            foreach (var row in dataArray.EnumerateArray())
            {
                var dataRow = dataTable.NewRow();
                int i = 0;
                foreach (var val in row.EnumerateArray())
                {
                    dataRow[i++] = val.ValueKind == JsonValueKind.Null ? DBNull.Value : val.GetString()!;
                }
                dataTable.Rows.Add(dataRow);
            }
        }

        return dataTable;
    }
}
