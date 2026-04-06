using System.Text.Json.Serialization;

namespace IngestionOrchestrator.Functions.Models;

public record StorageConfig(
    [property: JsonPropertyName("account_url")] string AccountUrl,
    [property: JsonPropertyName("container")] string Container,
    [property: JsonPropertyName("auth")] StorageAuthConfig Auth
);

public record StorageAuthConfig(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("tenant_id")] string? TenantId = null,
    [property: JsonPropertyName("client_id")] string? ClientId = null,
    [property: JsonPropertyName("cert_name")] string? CertName = null
);
