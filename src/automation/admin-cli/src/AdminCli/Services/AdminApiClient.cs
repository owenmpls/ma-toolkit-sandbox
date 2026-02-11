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
    private readonly AuthService? _authService;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };

    public AdminApiClient(IConfiguration configuration, AuthService? authService = null)
    {
        _configuration = configuration;
        _authService = authService;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public string? GetApiUrl(string? overrideUrl = null)
    {
        return overrideUrl
            ?? _configuration["API_URL"]
            ?? Environment.GetEnvironmentVariable("MATOOLKIT_API_URL");
    }

    private async Task<HttpClient> GetConfiguredClientAsync(string? apiUrl = null)
    {
        var baseUrl = GetApiUrl(apiUrl);
        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new InvalidOperationException(
                "API URL not configured. Set MATOOLKIT_API_URL environment variable or use --api-url option.");
        }

        _httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");

        if (_authService?.IsConfigured() == true)
        {
            var token = await _authService.GetAccessTokenAsync();
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return _httpClient;
    }

    #region Runbook Operations

    public async Task<RunbookResponse> PublishRunbookAsync(string name, string yamlContent, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        var request = new { name, yamlContent };
        var response = await client.PostAsJsonAsync("api/runbooks", request, JsonOptions);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<RunbookResponse>(JsonOptions))!;
    }

    public async Task<List<RunbookSummary>> ListRunbooksAsync(string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        var response = await client.GetAsync("api/runbooks");
        await EnsureSuccessAsync(response);
        var wrapper = (await response.Content.ReadFromJsonAsync<RunbookListResponse>(JsonOptions))!;
        return wrapper.Runbooks;
    }

    public async Task<RunbookResponse> GetRunbookAsync(string name, int? version = null, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        var url = version.HasValue
            ? $"api/runbooks/{name}/versions/{version}"
            : $"api/runbooks/{name}";
        var response = await client.GetAsync(url);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<RunbookResponse>(JsonOptions))!;
    }

    public async Task<List<RunbookVersionSummary>> ListRunbookVersionsAsync(string name, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        var response = await client.GetAsync($"api/runbooks/{name}/versions");
        await EnsureSuccessAsync(response);
        var wrapper = (await response.Content.ReadFromJsonAsync<RunbookVersionListResponse>(JsonOptions))!;
        return wrapper.Versions;
    }

    public async Task DeleteRunbookVersionAsync(string name, int version, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        var response = await client.DeleteAsync($"api/runbooks/{name}/versions/{version}");
        await EnsureSuccessAsync(response);
    }

    #endregion

    #region Automation Operations

    public async Task<AutomationStatus> GetAutomationStatusAsync(string runbookName, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        var response = await client.GetAsync($"api/runbooks/{runbookName}/automation");
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<AutomationStatus>(JsonOptions))!;
    }

    public async Task SetAutomationStatusAsync(string runbookName, bool enabled, string? user = null, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        var request = new { enabled, user = user ?? Environment.UserName };
        var response = await client.PutAsJsonAsync($"api/runbooks/{runbookName}/automation", request, JsonOptions);
        await EnsureSuccessAsync(response);
    }

    #endregion

    #region Query Operations

    public async Task<QueryPreviewResponse> PreviewQueryAsync(string runbookName, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        var response = await client.PostAsync($"api/runbooks/{runbookName}/query/preview", null);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<QueryPreviewResponse>(JsonOptions))!;
    }

    #endregion

    #region Template Operations

    public async Task<string> DownloadTemplateAsync(string runbookName, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        var response = await client.GetAsync($"api/runbooks/{runbookName}/template");
        await EnsureSuccessAsync(response);
        return await response.Content.ReadAsStringAsync();
    }

    #endregion

    #region Batch Operations

    public async Task<List<BatchSummary>> ListBatchesAsync(string? runbookName = null, string? status = null, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        var queryParams = new List<string>();
        if (!string.IsNullOrEmpty(runbookName)) queryParams.Add($"runbook={runbookName}");
        if (!string.IsNullOrEmpty(status)) queryParams.Add($"status={status}");

        var url = "api/batches";
        if (queryParams.Count > 0) url += "?" + string.Join("&", queryParams);

        var response = await client.GetAsync(url);
        await EnsureSuccessAsync(response);
        var wrapper = (await response.Content.ReadFromJsonAsync<BatchListResponse>(JsonOptions))!;
        return wrapper.Batches;
    }

    public async Task<BatchDetails> GetBatchAsync(int batchId, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        var response = await client.GetAsync($"api/batches/{batchId}");
        await EnsureSuccessAsync(response);
        var wrapper = (await response.Content.ReadFromJsonAsync<BatchGetResponse>(JsonOptions))!;
        return wrapper.Batch;
    }

    public async Task<CreateBatchResponse> CreateBatchAsync(string runbookName, string csvContent, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(runbookName), "runbookName");
        var csvBytes = System.Text.Encoding.UTF8.GetBytes(csvContent);
        content.Add(new ByteArrayContent(csvBytes), "file", "batch.csv");
        var response = await client.PostAsync("api/batches", content);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<CreateBatchResponse>(JsonOptions))!;
    }

    public async Task<AdvanceResponse> AdvanceBatchAsync(int batchId, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        var response = await client.PostAsync($"api/batches/{batchId}/advance", null);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<AdvanceResponse>(JsonOptions))!;
    }

    public async Task CancelBatchAsync(int batchId, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        var response = await client.PostAsync($"api/batches/{batchId}/cancel", null);
        await EnsureSuccessAsync(response);
    }

    #endregion

    #region Member Operations

    public async Task<List<MemberSummary>> ListMembersAsync(int batchId, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        var response = await client.GetAsync($"api/batches/{batchId}/members");
        await EnsureSuccessAsync(response);
        var wrapper = (await response.Content.ReadFromJsonAsync<MemberListResponse>(JsonOptions))!;
        return wrapper.Members;
    }

    public async Task<AddMembersResponse> AddMembersAsync(int batchId, string csvContent, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        using var content = new MultipartFormDataContent();
        var csvBytes = System.Text.Encoding.UTF8.GetBytes(csvContent);
        content.Add(new ByteArrayContent(csvBytes), "file", "members.csv");
        var response = await client.PostAsync($"api/batches/{batchId}/members", content);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<AddMembersResponse>(JsonOptions))!;
    }

    public async Task RemoveMemberAsync(int batchId, int memberId, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        var response = await client.DeleteAsync($"api/batches/{batchId}/members/{memberId}");
        await EnsureSuccessAsync(response);
    }

    #endregion

    #region Execution Tracking

    public async Task<List<PhaseExecution>> ListPhasesAsync(int batchId, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        var response = await client.GetAsync($"api/batches/{batchId}/phases");
        await EnsureSuccessAsync(response);
        var wrapper = (await response.Content.ReadFromJsonAsync<PhaseListResponse>(JsonOptions))!;
        return wrapper.Phases;
    }

    public async Task<List<StepExecution>> ListStepsAsync(int batchId, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);
        var response = await client.GetAsync($"api/batches/{batchId}/steps");
        await EnsureSuccessAsync(response);
        var wrapper = (await response.Content.ReadFromJsonAsync<StepListResponse>(JsonOptions))!;
        return wrapper.Steps;
    }

    #endregion

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpClient, Task<HttpResponseMessage>> requestFunc, string? apiUrl = null)
    {
        var client = await GetConfiguredClientAsync(apiUrl);

        for (int attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await requestFunc(client);
                var statusCode = (int)response.StatusCode;

                // Retry on 502, 503, 504 (transient server errors)
                if (attempt < MaxRetries && statusCode is 502 or 503 or 504)
                {
                    response.Dispose();
                    await Task.Delay(RetryDelays[attempt]);
                    continue;
                }

                await EnsureSuccessAsync(response);
                return response;
            }
            catch (HttpRequestException) when (attempt < MaxRetries && response is null)
            {
                // Connection-level failure (DNS, TCP reset, etc.) — retry
                await Task.Delay(RetryDelays[attempt]);
            }
            catch (TaskCanceledException) when (attempt < MaxRetries)
            {
                // Timeout — retry
                await Task.Delay(RetryDelays[attempt]);
            }
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"API request failed ({(int)response.StatusCode} {response.StatusCode}): {content}");
        }
    }
}
