using System.Text.Json;
using System.Text.Json.Serialization;
using IngestionOrchestrator.Functions.Models;
using IngestionOrchestrator.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace IngestionOrchestrator.Functions.Functions;

public class ManualRunFunction
{
    private readonly IConfigLoader _configLoader;
    private readonly IRunExecutor _runExecutor;
    private readonly ILogger<ManualRunFunction> _logger;

    public ManualRunFunction(
        IConfigLoader configLoader,
        IRunExecutor runExecutor,
        ILogger<ManualRunFunction> logger)
    {
        _configLoader = configLoader;
        _runExecutor = runExecutor;
        _logger = logger;
    }

    [Function("ManualRun")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "run")] HttpRequest req)
    {
        var body = await JsonSerializer.DeserializeAsync<ManualRunRequest>(req.Body);
        if (body == null)
            return new BadRequestObjectResult("Invalid request body");

        // Find job definition (optional — can run ad-hoc without a named job)
        JobDefinition job;
        if (!string.IsNullOrEmpty(body.JobName))
        {
            var found = _configLoader.Jobs.Jobs.FirstOrDefault(j => j.Name == body.JobName);
            if (found == null)
                return new NotFoundObjectResult($"Job '{body.JobName}' not found");
            job = found;
        }
        else
        {
            // Ad-hoc run: require either include_tiers or entity_names
            if (body.EntityNames is not { Count: > 0 } && body.IncludeTiers is not { Count: > 0 })
                return new BadRequestObjectResult(
                    "Either job_name, include_tiers, or entity_names is required");

            job = new JobDefinition(
                $"adhoc-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
                "Ad-hoc manual run",
                null,
                true,
                new EntitySelector(
                    IncludeTiers: body.IncludeTiers,
                    IncludeEntities: body.EntityNames,
                    ExcludeEntities: body.ExcludeEntities),
                new TenantSelector(body.TenantKeys is { Count: > 0 } ? "specific" : "all",
                    TenantKeys: body.TenantKeys));
        }

        var triggeredBy = req.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL-NAME", out var name)
            ? name.ToString() : "manual";

        _logger.LogInformation("Manual run triggered for job {Job} by {User}", job.Name, triggeredBy);

        // Build entity selector override if the request specifies entities/tiers
        EntitySelector? entityOverride = null;
        if (!string.IsNullOrEmpty(body.JobName) &&
            (body.IncludeTiers is { Count: > 0 } || body.EntityNames is { Count: > 0 }))
        {
            entityOverride = new EntitySelector(
                IncludeTiers: body.IncludeTiers,
                IncludeEntities: body.EntityNames,
                ExcludeEntities: body.ExcludeEntities);
        }

        // Dispatch ACA Jobs and return. Containers run independently —
        // the orchestrator doesn't poll or wait for completion.
        var result = await _runExecutor.ExecuteAsync(
            job, "manual", triggeredBy,
            body.TenantKeys, entityOverride,
            force: body.Force);

        return result switch
        {
            DispatchResult.SkippedOverlap => new ConflictObjectResult(new { job_name = job.Name,
                status = "skipped_overlap",
                message = $"Job '{job.Name}' already has an active run. Use force=true to override." }),
            DispatchResult.SkippedEmpty => new BadRequestObjectResult(new { job_name = job.Name,
                status = "skipped_empty",
                message = "Resolved to 0 tenants or 0 entities. Check tenant_keys and entity selectors." }),
            _ => new OkObjectResult(new { job_name = job.Name, status = "dispatched" })
        };
    }

    private record ManualRunRequest(
        [property: JsonPropertyName("job_name")] string? JobName = null,
        [property: JsonPropertyName("tenant_keys")] IReadOnlyList<string>? TenantKeys = null,
        [property: JsonPropertyName("include_tiers")] IReadOnlyList<int>? IncludeTiers = null,
        [property: JsonPropertyName("entity_names")] IReadOnlyList<string>? EntityNames = null,
        [property: JsonPropertyName("exclude_entities")] IReadOnlyList<string>? ExcludeEntities = null,
        [property: JsonPropertyName("force")] bool Force = false
    );
}
