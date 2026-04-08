using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using IngestionOrchestrator.Functions.Models;
using IngestionOrchestrator.Functions.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IngestionOrchestrator.Functions.Services;

public interface IContainerJobDispatcher
{
    Task<string> StartJobAsync(string containerJobName, TenantConfig tenant,
        IReadOnlyList<string> entityNames, StorageConfig storage);
    Task<string> GetExecutionStatusAsync(string containerJobName, string executionName);
}

public class ContainerJobDispatcher : IContainerJobDispatcher
{
    private const string ArmApiVersion = "2024-03-01";
    private const string ArmBaseUrl = "https://management.azure.com";

    private readonly HttpClient _httpClient;
    private readonly IngestionSettings _settings;
    private readonly IConfigLoader _configLoader;
    private readonly ILogger<ContainerJobDispatcher> _logger;
    private readonly DefaultAzureCredential _credential = new();

    public ContainerJobDispatcher(
        HttpClient httpClient,
        IOptions<IngestionSettings> settings,
        IConfigLoader configLoader,
        ILogger<ContainerJobDispatcher> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _configLoader = configLoader;
        _logger = logger;
    }

    public async Task<string> StartJobAsync(string containerJobName, TenantConfig tenant,
        IReadOnlyList<string> entityNames, StorageConfig storage)
    {
        await SetAuthHeaderAsync();

        // 1. Get current job config to read the container image
        var jobUrl = $"{ArmBaseUrl}/subscriptions/{_settings.SubscriptionId}/resourceGroups/{_settings.ResourceGroupName}" +
                     $"/providers/Microsoft.App/jobs/{containerJobName}?api-version={ArmApiVersion}";
        var jobResponse = await _httpClient.GetAsync(jobUrl);
        if (!jobResponse.IsSuccessStatusCode)
        {
            var errorBody = await jobResponse.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Failed to get ACA Job config for {containerJobName}: {jobResponse.StatusCode} - {errorBody}");
        }

        var jobJson = await jobResponse.Content.ReadAsStringAsync();
        var jobDoc = JsonDocument.Parse(jobJson);

        if (!jobDoc.RootElement.TryGetProperty("properties", out var props) ||
            !props.TryGetProperty("template", out var template) ||
            !template.TryGetProperty("containers", out var containers) ||
            containers.GetArrayLength() == 0 ||
            containers[0].GetProperty("image").GetString() is not { } image)
        {
            throw new InvalidOperationException(
                $"ACA Job {containerJobName} response missing expected properties.template.containers[0].image");
        }

        // 2. Build env vars
        var envVars = BuildEnvVars(tenant, entityNames, storage);

        // 3. Start job execution
        var startUrl = $"{ArmBaseUrl}/subscriptions/{_settings.SubscriptionId}/resourceGroups/{_settings.ResourceGroupName}" +
                       $"/providers/Microsoft.App/jobs/{containerJobName}/start?api-version={ArmApiVersion}";

        var body = new
        {
            containers = new[]
            {
                new
                {
                    name = "ingest",
                    image,
                    env = envVars.Select(kv => new { name = kv.Key, value = kv.Value }).ToArray()
                }
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var startResponse = await _httpClient.PostAsync(startUrl, content);
        if (!startResponse.IsSuccessStatusCode)
        {
            var errorBody = await startResponse.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Failed to start ACA Job {containerJobName}: {startResponse.StatusCode} - {errorBody}");
        }

        var startJson = await startResponse.Content.ReadAsStringAsync();
        var startDoc = JsonDocument.Parse(startJson);
        var executionId = startDoc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"ACA Job start response missing 'id' field");
        var executionName = executionId.Split('/').Last();

        _logger.LogInformation("Started ACA Job {Job} execution {Execution} for tenant {Tenant}",
            containerJobName, executionName, tenant.TenantKey);

        return executionName;
    }

    public async Task<string> GetExecutionStatusAsync(string containerJobName, string executionName)
    {
        await SetAuthHeaderAsync();

        var url = $"{ArmBaseUrl}/subscriptions/{_settings.SubscriptionId}/resourceGroups/{_settings.ResourceGroupName}" +
                  $"/providers/Microsoft.App/jobs/{containerJobName}/executions/{executionName}?api-version={ArmApiVersion}";

        var response = await _httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Failed to get execution status for {containerJobName}/{executionName}: {response.StatusCode} - {errorBody}");
        }

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("properties").GetProperty("status").GetString()
            ?? throw new InvalidOperationException($"Execution status response missing properties.status");
    }

    private Dictionary<string, string> BuildEnvVars(TenantConfig tenant,
        IReadOnlyList<string> entityNames, StorageConfig storage)
    {
        var vars = new Dictionary<string, string>
        {
            ["TENANT_KEY"] = tenant.TenantKey,
            ["TENANT_ID"] = tenant.TenantId,
            ["ORGANIZATION"] = tenant.Organization,
            ["CLIENT_ID"] = tenant.ClientId,
            ["CERT_NAME"] = tenant.CertName,
            ["ENTITY_NAMES"] = string.Join(",", entityNames),
            ["KEYVAULT_NAME"] = _settings.KeyVaultName,
            ["MAX_PARALLELISM"] = tenant.MaxParallelism.ToString(),
            ["SIGN_IN_LOOKBACK_DAYS"] = tenant.SignInLookbackDays.ToString(),
            ["STORAGE_ACCOUNT_URL"] = storage.AccountUrl,
            ["LANDING_CONTAINER"] = storage.Container,
            ["STORAGE_AUTH_METHOD"] = storage.Auth.Method
        };

        if (tenant.AdminUrl is not null)
            vars["ADMIN_URL"] = tenant.AdminUrl;

        if (storage.Auth.Method == "service_principal")
        {
            vars["STORAGE_SP_TENANT_ID"] = storage.Auth.TenantId!;
            vars["STORAGE_SP_CLIENT_ID"] = storage.Auth.ClientId!;
            vars["STORAGE_SP_CERT_NAME"] = storage.Auth.CertName!;
        }

        return vars;
    }

    private async Task SetAuthHeaderAsync()
    {
        var token = await _credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(["https://management.azure.com/.default"]));
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.Token);
    }
}
