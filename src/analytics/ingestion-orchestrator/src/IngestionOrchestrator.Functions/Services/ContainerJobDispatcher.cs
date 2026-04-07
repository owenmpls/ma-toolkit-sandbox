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
        jobResponse.EnsureSuccessStatusCode();
        var jobJson = await jobResponse.Content.ReadAsStringAsync();
        var jobDoc = JsonDocument.Parse(jobJson);
        var image = jobDoc.RootElement
            .GetProperty("properties").GetProperty("template").GetProperty("containers")[0]
            .GetProperty("image").GetString()!;

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
        startResponse.EnsureSuccessStatusCode();

        var startJson = await startResponse.Content.ReadAsStringAsync();
        var startDoc = JsonDocument.Parse(startJson);
        var executionId = startDoc.RootElement.GetProperty("id").GetString()!;
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
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("properties").GetProperty("status").GetString()!;
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
