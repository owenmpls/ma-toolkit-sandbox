using System.Text.Json.Serialization;

namespace IngestionOrchestrator.Functions.Models;

public record RunRecord(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("job_name")] string JobName,
    [property: JsonPropertyName("trigger_type")] string TriggerType,
    [property: JsonPropertyName("triggered_by")] string? TriggeredBy,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("tenant_count")] int TenantCount,
    [property: JsonPropertyName("entity_count")] int EntityCount,
    [property: JsonPropertyName("resolved_entities")] IReadOnlyList<string> ResolvedEntities,
    [property: JsonPropertyName("resolved_tenants")] IReadOnlyList<string> ResolvedTenants
);
