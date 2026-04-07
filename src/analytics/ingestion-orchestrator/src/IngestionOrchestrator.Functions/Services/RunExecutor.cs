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
    private readonly IRunTracker _runTracker;
    private readonly IConfigLoader _configLoader;
    private readonly ILogger<RunExecutor> _logger;

    public RunExecutor(
        IEntityResolver entityResolver,
        ITenantResolver tenantResolver,
        IContainerJobDispatcher dispatcher,
        IRunTracker runTracker,
        IConfigLoader configLoader,
        ILogger<RunExecutor> logger)
    {
        _entityResolver = entityResolver;
        _tenantResolver = tenantResolver;
        _dispatcher = dispatcher;
        _runTracker = runTracker;
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

        // Dispatch container jobs and build tracked tasks
        var trackedTasks = new List<TrackedTask>();

        foreach (var tenant in tenants)
        {
            foreach (var (containerType, entityNames) in entityGroups)
            {
                var task = new TrackedTask
                {
                    ContainerJobName = containerType,
                    TenantKey = tenant.TenantKey,
                    ContainerType = containerType,
                    Entities = entityNames
                };

                try
                {
                    var executionName = await _dispatcher.StartJobAsync(
                        containerType, tenant, entityNames, _configLoader.Storage);

                    task.AcaExecutionName = executionName;

                    _logger.LogInformation(
                        "Dispatched {Container} for tenant {Tenant} (execution: {Execution})",
                        containerType, tenant.TenantKey, executionName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start {Container} for tenant {Tenant}",
                        containerType, tenant.TenantKey);
                    task.Status = "dispatch_failed";
                    task.CompletedAt = DateTimeOffset.UtcNow;
                    task.ErrorMessage = ex.Message;
                }

                trackedTasks.Add(task);
            }
        }

        var dispatched = trackedTasks.Count(t => t.Status == "dispatched");
        var failed = trackedTasks.Count(t => t.Status == "dispatch_failed");

        _logger.LogInformation(
            "Run {RunId} for job {Job}: dispatched {Dispatched} jobs ({Failed} failed to dispatch)",
            runId, job.Name, dispatched, failed);

        // Register with tracker — timer will check status on subsequent ticks
        _runTracker.Track(new TrackedRun
        {
            RunId = runId,
            JobName = job.Name,
            TriggerType = triggerType,
            TriggeredBy = triggeredBy,
            StartedAt = startedAt,
            ResolvedEntities = entities.Select(e => e.Name).ToList(),
            ResolvedTenants = tenants.Select(t => t.TenantKey).ToList(),
            Tasks = trackedTasks
        });
    }
}
