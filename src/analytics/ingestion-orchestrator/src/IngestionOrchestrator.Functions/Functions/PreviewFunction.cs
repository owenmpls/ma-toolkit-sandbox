using System.Text.Json;
using System.Text.Json.Serialization;
using IngestionOrchestrator.Functions.Models;
using IngestionOrchestrator.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace IngestionOrchestrator.Functions.Functions;

public class PreviewFunction
{
    private readonly IConfigLoader _configLoader;
    private readonly IEntityResolver _entityResolver;
    private readonly ITenantResolver _tenantResolver;

    public PreviewFunction(
        IConfigLoader configLoader,
        IEntityResolver entityResolver,
        ITenantResolver tenantResolver)
    {
        _configLoader = configLoader;
        _entityResolver = entityResolver;
        _tenantResolver = tenantResolver;
    }

    [Function("Preview")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "preview")] HttpRequest req)
    {
        var body = await JsonSerializer.DeserializeAsync<PreviewRequest>(req.Body);
        if (body == null)
            return new BadRequestObjectResult("Invalid request body");

        EntitySelector entitySelector;
        TenantSelector tenantSelector;

        if (!string.IsNullOrEmpty(body.JobName))
        {
            var job = _configLoader.Jobs.Jobs.FirstOrDefault(j => j.Name == body.JobName);
            if (job == null)
                return new NotFoundObjectResult($"Job '{body.JobName}' not found");
            entitySelector = job.EntitySelector;
            tenantSelector = job.TenantSelector;
        }
        else
        {
            entitySelector = body.EntitySelector ?? new EntitySelector();
            tenantSelector = body.TenantSelector ?? new TenantSelector("all");
        }

        var entities = _entityResolver.Resolve(entitySelector);
        var tenants = _tenantResolver.Resolve(tenantSelector);

        var containerGroups = entities
            .GroupBy(e => e.Container)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Name).ToList());

        return new OkObjectResult(new
        {
            tenants = tenants.Select(t => new { t.TenantKey, t.Organization }),
            tenant_count = tenants.Count,
            entities = entities.Select(e => new { e.Name, e.Tier, e.Container }),
            entity_count = entities.Count,
            container_groups = containerGroups,
            task_count = tenants.Count * containerGroups.Count
        });
    }

    [Function("Config")]
    public IActionResult GetConfig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config")] HttpRequest req)
    {
        return new OkObjectResult(new
        {
            entity_registry = _configLoader.EntityRegistry,
            tenants = _configLoader.Tenants,
            jobs = _configLoader.Jobs,
            storage = new
            {
                _configLoader.Storage.AccountUrl,
                _configLoader.Storage.Container,
                auth_method = _configLoader.Storage.Auth.Method
            }
        });
    }

    private record PreviewRequest(
        [property: JsonPropertyName("job_name")] string? JobName = null,
        [property: JsonPropertyName("entity_selector")] EntitySelector? EntitySelector = null,
        [property: JsonPropertyName("tenant_selector")] TenantSelector? TenantSelector = null
    );
}
