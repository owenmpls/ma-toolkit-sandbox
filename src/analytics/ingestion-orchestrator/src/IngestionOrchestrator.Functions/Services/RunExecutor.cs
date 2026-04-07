using IngestionOrchestrator.Functions.Models;
using Microsoft.Extensions.Logging;

namespace IngestionOrchestrator.Functions.Services;

public interface IRunExecutor
{
    Task ExecuteAsync(JobDefinition job, string triggerType, string? triggeredBy,
        IReadOnlyList<string>? tenantKeyOverrides = null,
        EntitySelector? entitySelectorOverride = null);
}

public class RunExecutor : IRunExecutor
{
    private readonly IEntityResolver _entityResolver;
    private readonly ITenantResolver _tenantResolver;
    private readonly IContainerJobDispatcher _dispatcher;
    private readonly IRunHistoryWriter _historyWriter;
    private readonly IConfigLoader _configLoader;
    private readonly ILogger<RunExecutor> _logger;

    public RunExecutor(
        IEntityResolver entityResolver,
        ITenantResolver tenantResolver,
        IContainerJobDispatcher dispatcher,
        IRunHistoryWriter historyWriter,
        IConfigLoader configLoader,
        ILogger<RunExecutor> logger)
    {
        _entityResolver = entityResolver;
        _tenantResolver = tenantResolver;
        _dispatcher = dispatcher;
        _historyWriter = historyWriter;
        _configLoader = configLoader;
        _logger = logger;
    }

    public async Task ExecuteAsync(JobDefinition job, string triggerType, string? triggeredBy,
        IReadOnlyList<string>? tenantKeyOverrides = null,
        EntitySelector? entitySelectorOverride = null)
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        var startedAt = DateTimeOffset.UtcNow;

        // Resolve tenants
        IReadOnlyList<TenantConfig> tenants;
        if (tenantKeyOverrides is { Count: > 0 })
        {
            var allTenants = _tenantResolver.Resolve(new TenantSelector("all"));
            var keySet = tenantKeyOverrides.ToHashSet();
            tenants = allTenants.Where(t => keySet.Contains(t.TenantKey)).ToList();
        }
        else
        {
            tenants = _tenantResolver.Resolve(job.TenantSelector);
        }

        // Resolve entities
        var entitySelector = entitySelectorOverride ?? job.EntitySelector;
        var entities = _entityResolver.Resolve(entitySelector);

        if (tenants.Count == 0 || entities.Count == 0)
        {
            _logger.LogWarning("Job {Job} resolved to 0 tenants or 0 entities, skipping", job.Name);
            return;
        }

        // Group entities by container type
        var entityGroups = entities.GroupBy(e => e.Container)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Name).ToList());

        _logger.LogInformation(
            "Run {RunId} for job {Job}: dispatching {TaskCount} container jobs across {TenantCount} tenants ({EntityCount} entities)",
            runId, job.Name, tenants.Count * entityGroups.Count, tenants.Count, entities.Count);

        // Dispatch container jobs
        var taskRecords = new List<TaskRecord>();

        foreach (var tenant in tenants)
        {
            foreach (var (containerType, entityNames) in entityGroups)
            {
                var taskRecord = new TaskRecord(
                    runId, job.Name, tenant.TenantKey, containerType,
                    null, entityNames, "dispatched",
                    DateTimeOffset.UtcNow, null, null);

                try
                {
                    var executionName = await _dispatcher.StartJobAsync(
                        containerType, tenant, entityNames, _configLoader.Storage);

                    taskRecord = taskRecord with { AcaExecutionName = executionName };

                    _logger.LogInformation(
                        "Dispatched {Container} for tenant {Tenant} (execution: {Execution})",
                        containerType, tenant.TenantKey, executionName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start {Container} for tenant {Tenant}",
                        containerType, tenant.TenantKey);
                    taskRecord = taskRecord with
                    {
                        Status = "dispatch_failed",
                        CompletedAt = DateTimeOffset.UtcNow,
                        ErrorMessage = ex.Message
                    };
                }

                taskRecords.Add(taskRecord);
            }
        }

        var dispatched = taskRecords.Count(t => t.Status == "dispatched");
        var failed = taskRecords.Count(t => t.Status == "dispatch_failed");

        _logger.LogInformation(
            "Run {RunId} for job {Job}: dispatched {Dispatched} jobs ({Failed} failed to dispatch)",
            runId, job.Name, dispatched, failed);

        // Write run history (best-effort — containers write their own per-entity manifests)
        await _historyWriter.WriteRunAsync(new RunRecord(
            runId, job.Name, triggerType, triggeredBy, "dispatched",
            startedAt, DateTimeOffset.UtcNow,
            tenants.Count, entities.Count,
            entities.Select(e => e.Name).ToList(),
            tenants.Select(t => t.TenantKey).ToList()));

        await _historyWriter.WriteTasksAsync(runId, taskRecords);
    }
}
