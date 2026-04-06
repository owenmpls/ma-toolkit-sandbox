using System.Collections.Concurrent;
using IngestionOrchestrator.Functions.Models;
using Microsoft.Extensions.Logging;

namespace IngestionOrchestrator.Functions.Services;

public interface IRunExecutor
{
    ConcurrentDictionary<string, ActiveRun> ActiveRuns { get; }

    Task ExecuteAsync(JobDefinition job, string triggerType, string? triggeredBy,
        IReadOnlyList<string>? tenantKeyOverrides = null,
        EntitySelector? entitySelectorOverride = null);
}

public record ActiveRun(string RunId, DateTimeOffset StartedAt);

public class RunExecutor : IRunExecutor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromHours(8);

    private readonly IEntityResolver _entityResolver;
    private readonly ITenantResolver _tenantResolver;
    private readonly IContainerJobDispatcher _dispatcher;
    private readonly IRunHistoryWriter _historyWriter;
    private readonly IConfigLoader _configLoader;
    private readonly ILogger<RunExecutor> _logger;

    public ConcurrentDictionary<string, ActiveRun> ActiveRuns { get; } = new();

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

        // Track active run
        var activeRun = new ActiveRun(runId, startedAt);
        if (!ActiveRuns.TryAdd(job.Name, activeRun))
        {
            _logger.LogWarning("Job {Job} skipped — run {ActiveRun} still active",
                job.Name, ActiveRuns[job.Name].RunId);

            await _historyWriter.WriteRunAsync(new RunRecord(
                runId, job.Name, triggerType, triggeredBy, "skipped",
                startedAt, DateTimeOffset.UtcNow,
                tenants.Count, entities.Count,
                entities.Select(e => e.Name).ToList(),
                tenants.Select(t => t.TenantKey).ToList()));
            return;
        }

        try
        {
            _logger.LogInformation(
                "Starting run {RunId} for job {Job}: {TenantCount} tenants, {EntityCount} entities",
                runId, job.Name, tenants.Count, entities.Count);

            // Write initial run record
            await _historyWriter.WriteRunAsync(new RunRecord(
                runId, job.Name, triggerType, triggeredBy, "running",
                startedAt, null, tenants.Count, entities.Count,
                entities.Select(e => e.Name).ToList(),
                tenants.Select(t => t.TenantKey).ToList()));

            // Group entities by container type
            var entityGroups = entities.GroupBy(e => e.Container)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Name).ToList());

            // Dispatch container jobs
            var taskRecords = new List<TaskRecord>();
            var dispatched = new List<(TaskRecord Record, string ContainerJobName, string ExecutionName)>();

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

                        taskRecord = taskRecord with
                        {
                            AcaExecutionName = executionName,
                            Status = "running"
                        };
                        dispatched.Add((taskRecord, containerType, executionName));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to start {Container} for tenant {Tenant}",
                            containerType, tenant.TenantKey);
                        taskRecord = taskRecord with
                        {
                            Status = "failed",
                            CompletedAt = DateTimeOffset.UtcNow,
                            ErrorMessage = ex.Message
                        };
                    }

                    taskRecords.Add(taskRecord);
                }
            }

            // Poll for completion
            var pending = dispatched.ToList();
            var deadline = DateTimeOffset.UtcNow + PollTimeout;

            while (pending.Count > 0 && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(PollInterval);

                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var (record, containerJobName, executionName) = pending[i];
                    try
                    {
                        var status = await _dispatcher.GetExecutionStatusAsync(
                            containerJobName, executionName);

                        if (status is "Succeeded" or "Failed")
                        {
                            var idx = taskRecords.FindIndex(t =>
                                t.TenantKey == record.TenantKey &&
                                t.ContainerType == record.ContainerType);

                            taskRecords[idx] = record with
                            {
                                Status = status == "Succeeded" ? "succeeded" : "failed",
                                CompletedAt = DateTimeOffset.UtcNow,
                                ErrorMessage = status == "Failed"
                                    ? $"ACA Job execution {executionName} failed" : null
                            };

                            pending.RemoveAt(i);
                            _logger.LogInformation(
                                "Task {Container}/{Tenant} completed: {Status}",
                                containerJobName, record.TenantKey, status);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error polling {Container}/{Tenant}",
                            containerJobName, record.TenantKey);
                    }
                }
            }

            // Mark timed-out tasks as failed
            foreach (var (record, containerJobName, _) in pending)
            {
                var idx = taskRecords.FindIndex(t =>
                    t.TenantKey == record.TenantKey &&
                    t.ContainerType == record.ContainerType);
                taskRecords[idx] = record with
                {
                    Status = "failed",
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = "Timed out waiting for ACA Job completion"
                };
            }

            // Write task records
            await _historyWriter.WriteTasksAsync(runId, taskRecords);

            // Write final run record
            var hasFailures = taskRecords.Any(t => t.Status == "failed");
            var allFailed = taskRecords.All(t => t.Status == "failed");
            var finalStatus = allFailed ? "failed"
                : hasFailures ? "completed_with_errors"
                : "completed";

            await _historyWriter.WriteRunAsync(new RunRecord(
                runId, job.Name, triggerType, triggeredBy, finalStatus,
                startedAt, DateTimeOffset.UtcNow,
                tenants.Count, entities.Count,
                entities.Select(e => e.Name).ToList(),
                tenants.Select(t => t.TenantKey).ToList()));

            _logger.LogInformation("Run {RunId} for job {Job} finished: {Status}",
                runId, job.Name, finalStatus);
        }
        finally
        {
            ActiveRuns.TryRemove(job.Name, out _);
        }
    }
}
