using System.Data;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Scheduler.Functions.Services;

public interface IDatabricksQueryClient
{
    Task<DataTable> ExecuteQueryAsync(string connectionEnvVar, string warehouseIdEnvVar, string query);
}

public class DatabricksQueryClient : IDatabricksQueryClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<DatabricksQueryClient> _logger;

    public DatabricksQueryClient(HttpClient httpClient, IConfiguration config, ILogger<DatabricksQueryClient> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<DataTable> ExecuteQueryAsync(string connectionEnvVar, string warehouseIdEnvVar, string query)
    {
        var workspaceUrl = _config[connectionEnvVar]
            ?? throw new InvalidOperationException($"Configuration '{connectionEnvVar}' not found");
        var warehouseId = _config[warehouseIdEnvVar]
            ?? throw new InvalidOperationException($"Configuration '{warehouseIdEnvVar}' not found");

        _logger.LogInformation("Executing Databricks SQL query on warehouse {WarehouseId}", warehouseId);

        var credential = new DefaultAzureCredential();
        var token = await credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(new[] { "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default" }));

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        var requestBody = new
        {
            warehouse_id = warehouseId,
            statement = query,
            wait_timeout = "120s",
            disposition = "INLINE"
        };

        var response = await _httpClient.PostAsync(
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
            result = await PollForCompletionAsync(workspaceUrl, statementId!);
        }

        return BuildDataTable(result);
    }

    private async Task<JsonDocument> PollForCompletionAsync(string workspaceUrl, string statementId)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));

            var response = await _httpClient.GetAsync(
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
                    throw new InvalidOperationException($"Databricks query {status}: {error}");
                default:
                    _logger.LogDebug("Databricks statement {StatementId} status: {Status}", statementId, status);
                    continue;
            }
        }
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
