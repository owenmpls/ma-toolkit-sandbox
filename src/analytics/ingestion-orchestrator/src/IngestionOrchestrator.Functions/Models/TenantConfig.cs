using System.Text.Json.Serialization;

namespace IngestionOrchestrator.Functions.Models;

public record TenantConfig(
    [property: JsonPropertyName("tenant_key")] string TenantKey,
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("organization")] string Organization,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("cert_name")] string CertName,
    [property: JsonPropertyName("admin_url")] string? AdminUrl,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("max_parallelism")] int MaxParallelism,
    [property: JsonPropertyName("sign_in_lookback_days")] int SignInLookbackDays
);

public record TenantsConfig(
    [property: JsonPropertyName("tenants")] IReadOnlyList<TenantConfig> Tenants
);
