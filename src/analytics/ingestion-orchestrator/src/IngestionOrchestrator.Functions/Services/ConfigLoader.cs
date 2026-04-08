using System.Text.Json;
using IngestionOrchestrator.Functions.Models;

namespace IngestionOrchestrator.Functions.Services;

public interface IConfigLoader
{
    EntityRegistryConfig EntityRegistry { get; }
    TenantsConfig Tenants { get; }
    JobsConfig Jobs { get; }
    StorageConfig Storage { get; }
}

public class ConfigLoader : IConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public EntityRegistryConfig EntityRegistry { get; }
    public TenantsConfig Tenants { get; }
    public JobsConfig Jobs { get; }
    public StorageConfig Storage { get; }

    public ConfigLoader(string configPath)
    {
        EntityRegistry = Load<EntityRegistryConfig>(configPath, "entity-registry.json");
        Tenants = Load<TenantsConfig>(configPath, "tenants.json");
        Jobs = Load<JobsConfig>(configPath, "jobs.json");
        Storage = Load<StorageConfig>(configPath, "storage.json");

        Validate();
    }

    private static T Load<T>(string configPath, string fileName)
    {
        var filePath = Path.Combine(configPath, fileName);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Config file not found: {filePath}");

        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize {fileName}");
    }

    private void Validate()
    {
        var entityNames = EntityRegistry.Entities.Select(e => e.Name).ToHashSet();

        // Validate entity registry
        var duplicateEntities = EntityRegistry.Entities
            .GroupBy(e => e.Name)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateEntities.Count > 0)
            throw new InvalidOperationException(
                $"Duplicate entities in entity-registry.json: {string.Join(", ", duplicateEntities)}");

        foreach (var entity in EntityRegistry.Entities)
        {
            if (entity.Tier is < 1 or > 5)
                throw new InvalidOperationException(
                    $"Entity '{entity.Name}' has invalid tier {entity.Tier} (must be 1-5)");
        }

        // Validate tenant keys are unique
        var duplicateTenants = Tenants.Tenants
            .GroupBy(t => t.TenantKey)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateTenants.Count > 0)
            throw new InvalidOperationException(
                $"Duplicate tenant keys in tenants.json: {string.Join(", ", duplicateTenants)}");

        // Validate storage auth
        if (Storage.Auth.Method is not ("managed_identity" or "service_principal"))
            throw new InvalidOperationException(
                $"Invalid storage auth method: '{Storage.Auth.Method}' (must be 'managed_identity' or 'service_principal')");

        if (Storage.Auth.Method == "service_principal")
        {
            if (string.IsNullOrWhiteSpace(Storage.Auth.TenantId))
                throw new InvalidOperationException("Storage auth: service_principal requires tenant_id");
            if (string.IsNullOrWhiteSpace(Storage.Auth.ClientId))
                throw new InvalidOperationException("Storage auth: service_principal requires client_id");
            if (string.IsNullOrWhiteSpace(Storage.Auth.CertName))
                throw new InvalidOperationException("Storage auth: service_principal requires cert_name");
        }

        // Validate job references
        var tenantKeys = Tenants.Tenants.Select(t => t.TenantKey).ToHashSet();

        foreach (var job in Jobs.Jobs)
        {
            if (string.IsNullOrWhiteSpace(job.Name))
                throw new InvalidOperationException("Job name cannot be empty");

            // Validate entity references
            foreach (var include in job.EntitySelector.IncludeEntities ?? [])
            {
                if (!entityNames.Contains(include))
                    throw new InvalidOperationException(
                        $"Job '{job.Name}' references unknown entity '{include}' in include_entities");
            }
            foreach (var exclude in job.EntitySelector.ExcludeEntities ?? [])
            {
                if (!entityNames.Contains(exclude))
                    throw new InvalidOperationException(
                        $"Job '{job.Name}' references unknown entity '{exclude}' in exclude_entities");
            }

            // Validate tenant selector references
            foreach (var key in job.TenantSelector.TenantKeys ?? [])
            {
                if (!tenantKeys.Contains(key))
                    throw new InvalidOperationException(
                        $"Job '{job.Name}' references unknown tenant '{key}' in tenant_keys");
            }
            foreach (var key in job.TenantSelector.ExcludeKeys ?? [])
            {
                if (!tenantKeys.Contains(key))
                    throw new InvalidOperationException(
                        $"Job '{job.Name}' references unknown tenant '{key}' in exclude_keys");
            }

            // Validate tenant selector mode has required fields
            if (job.TenantSelector.Mode == "specific" && (job.TenantSelector.TenantKeys == null || job.TenantSelector.TenantKeys.Count == 0))
                throw new InvalidOperationException(
                    $"Job '{job.Name}' uses mode 'specific' but has no tenant_keys");
            if (job.TenantSelector.Mode == "all_except" && (job.TenantSelector.ExcludeKeys == null || job.TenantSelector.ExcludeKeys.Count == 0))
                throw new InvalidOperationException(
                    $"Job '{job.Name}' uses mode 'all_except' but has no exclude_keys");

            // Validate include_tiers range
            foreach (var tier in job.EntitySelector.IncludeTiers ?? [])
            {
                if (tier is < 1 or > 5)
                    throw new InvalidOperationException(
                        $"Job '{job.Name}' has invalid include_tier {tier} (must be 1-5)");
            }
        }
    }
}
