using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdminCli.Models;
using Microsoft.Extensions.Configuration;

namespace AdminCli.Services;

public class AdminApiClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AdminApiClient(IConfiguration configuration)
    {
        _configuration = configuration;
        _httpClient = new HttpClient();
    }

    public string? GetApiUrl(string? overrideUrl = null)
    {
        return overrideUrl
            ?? _configuration["API_URL"]
            ?? Environment.GetEnvironmentVariable("MATOOLKIT_API_URL");
    }

    private HttpClient GetConfiguredClient(string? apiUrl = null)
    {
        var baseUrl = GetApiUrl(apiUrl);
        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new InvalidOperationException(
                "API URL not configured. Set MATOOLKIT_API_URL environment variable or use --api-url option.");
        }

        _httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        return _httpClient;
    }

    #region Runbook Operations

    public async Task<RunbookResponse> PublishRunbookAsync(string name, string yamlContent, string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var request = new { name, yamlContent };
        var response = await client.PostAsJsonAsync("api/runbooks", request, JsonOptions);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<RunbookResponse>(JsonOptions))!;
    }

    public async Task<List<RunbookSummary>> ListRunbooksAsync(string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var response = await client.GetAsync("api/runbooks");
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<List<RunbookSummary>>(JsonOptions))!;
    }

    public async Task<RunbookResponse> GetRunbookAsync(string name, int? version = null, string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var url = version.HasValue
            ? $"api/runbooks/{name}/versions/{version}"
            : $"api/runbooks/{name}";
        var response = await client.GetAsync(url);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<RunbookResponse>(JsonOptions))!;
    }

    public async Task<List<RunbookVersionSummary>> ListRunbookVersionsAsync(string name, string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var response = await client.GetAsync($"api/runbooks/{name}/versions");
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<List<RunbookVersionSummary>>(JsonOptions))!;
    }

    public async Task DeleteRunbookVersionAsync(string name, int version, string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var response = await client.DeleteAsync($"api/runbooks/{name}/versions/{version}");
        await EnsureSuccessAsync(response);
    }

    #endregion

    #region Automation Operations

    public async Task<AutomationStatus> GetAutomationStatusAsync(string runbookName, string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var response = await client.GetAsync($"api/runbooks/{runbookName}/automation");
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<AutomationStatus>(JsonOptions))!;
    }

    public async Task SetAutomationStatusAsync(string runbookName, bool enabled, string? user = null, string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var request = new { enabled, user = user ?? Environment.UserName };
        var response = await client.PutAsJsonAsync($"api/runbooks/{runbookName}/automation", request, JsonOptions);
        await EnsureSuccessAsync(response);
    }

    #endregion

    #region Query Operations

    public async Task<QueryPreviewResponse> PreviewQueryAsync(string runbookName, string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var response = await client.PostAsync($"api/runbooks/{runbookName}/query/preview", null);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<QueryPreviewResponse>(JsonOptions))!;
    }

    #endregion

    #region Template Operations

    public async Task<string> DownloadTemplateAsync(string runbookName, string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var response = await client.GetAsync($"api/runbooks/{runbookName}/template");
        await EnsureSuccessAsync(response);
        return await response.Content.ReadAsStringAsync();
    }

    #endregion

    #region Batch Operations

    public async Task<List<BatchSummary>> ListBatchesAsync(string? runbookName = null, string? status = null, string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var queryParams = new List<string>();
        if (!string.IsNullOrEmpty(runbookName)) queryParams.Add($"runbook={runbookName}");
        if (!string.IsNullOrEmpty(status)) queryParams.Add($"status={status}");

        var url = "api/batches";
        if (queryParams.Count > 0) url += "?" + string.Join("&", queryParams);

        var response = await client.GetAsync(url);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<List<BatchSummary>>(JsonOptions))!;
    }

    public async Task<BatchDetails> GetBatchAsync(int batchId, string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var response = await client.GetAsync($"api/batches/{batchId}");
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<BatchDetails>(JsonOptions))!;
    }

    public async Task<CreateBatchResponse> CreateBatchAsync(string runbookName, string csvContent, string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var request = new { runbookName, csvContent };
        var response = await client.PostAsJsonAsync("api/batches", request, JsonOptions);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<CreateBatchResponse>(JsonOptions))!;
    }

    public async Task<AdvanceResponse> AdvanceBatchAsync(int batchId, string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var response = await client.PostAsync($"api/batches/{batchId}/advance", null);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<AdvanceResponse>(JsonOptions))!;
    }

    public async Task CancelBatchAsync(int batchId, string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var response = await client.PostAsync($"api/batches/{batchId}/cancel", null);
        await EnsureSuccessAsync(response);
    }

    #endregion

    #region Member Operations

    public async Task<List<MemberSummary>> ListMembersAsync(int batchId, string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var response = await client.GetAsync($"api/batches/{batchId}/members");
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<List<MemberSummary>>(JsonOptions))!;
    }

    public async Task<AddMembersResponse> AddMembersAsync(int batchId, string csvContent, string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var request = new { csvContent };
        var response = await client.PostAsJsonAsync($"api/batches/{batchId}/members", request, JsonOptions);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<AddMembersResponse>(JsonOptions))!;
    }

    public async Task RemoveMemberAsync(int batchId, int memberId, string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var response = await client.DeleteAsync($"api/batches/{batchId}/members/{memberId}");
        await EnsureSuccessAsync(response);
    }

    #endregion

    #region Execution Tracking

    public async Task<List<PhaseExecution>> ListPhasesAsync(int batchId, string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var response = await client.GetAsync($"api/batches/{batchId}/phases");
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<List<PhaseExecution>>(JsonOptions))!;
    }

    public async Task<List<StepExecution>> ListStepsAsync(int batchId, string? apiUrl = null)
    {
        var client = GetConfiguredClient(apiUrl);
        var response = await client.GetAsync($"api/batches/{batchId}/steps");
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<List<StepExecution>>(JsonOptions))!;
    }

    #endregion

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"API request failed ({(int)response.StatusCode} {response.StatusCode}): {content}");
        }
    }
}
