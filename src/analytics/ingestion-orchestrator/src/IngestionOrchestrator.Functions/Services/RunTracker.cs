using System.Collections.Concurrent;
using IngestionOrchestrator.Functions.Models;
using Microsoft.Extensions.Logging;

namespace IngestionOrchestrator.Functions.Services;

public interface IRunTracker
{
    void Track(TrackedRun run);
    Task CheckActiveRunsAsync();
}

public record TrackedRun
{
    public required string RunId { get; init; }
    public required string JobName { get; init; }
    public required string TriggerType { get; init; }
    public string? TriggeredBy { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required IReadOnlyList<string> ResolvedEntities { get; init; }
    public required IReadOnlyList<string> ResolvedTenants { get; init; }
    public required List<TrackedTask> Tasks { get; init; }
}

public record TrackedTask
{
    public required string ContainerJobName { get; init; }
    public required string TenantKey { get; init; }
    public required string ContainerType { get; init; }
    public required IReadOnlyList<string> Entities { get; init; }
    public string? AcaExecutionName { get; set; }
    public string Status { get; set; } = "dispatched";
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

public class RunTracker : IRunTracker
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromHours(8);

    private readonly ConcurrentDictionary<string, TrackedRun> _activeRuns = new();
    private readonly IContainerJobDispatcher _dispatcher;
    private readonly IRunHistoryWriter _historyWriter;
    private readonly ILogger<RunTracker> _logger;

    public RunTracker(
        IContainerJobDispatcher dispatcher,
        IRunHistoryWriter historyWriter,
        ILogger<RunTracker> logger)
    {
        _dispatcher = dispatcher;
        _historyWriter = historyWriter;
        _logger = logger;
    }

    public void Track(TrackedRun run)
    {
        _activeRuns[run.RunId] = run;
        _logger.LogInformation("Tracking run {RunId} ({TaskCount} tasks)", run.RunId, run.Tasks.Count);
    }

    public async Task CheckActiveRunsAsync()
    {
        if (_activeRuns.IsEmpty) return;

        _logger.LogInformation("Checking {Count} active run(s)", _activeRuns.Count);

        var completedRunIds = new List<string>();

        foreach (var (runId, run) in _activeRuns)
        {
            // Check for timeout
            if (DateTimeOffset.UtcNow - run.StartedAt > RunTimeout)
            {
                _logger.LogWarning("Run {RunId} timed out after {Hours}h", runId, RunTimeout.TotalHours);
                foreach (var task in run.Tasks.Where(t => t.Status == "dispatched"))
                {
                    task.Status = "timeout";
                    task.CompletedAt = DateTimeOffset.UtcNow;
                    task.ErrorMessage = "Run timed out";
                }
                completedRunIds.Add(runId);
                continue;
            }

            // Check each pending task
            var pendingTasks = run.Tasks.Where(t => t.Status == "dispatched").ToList();
            foreach (var task in pendingTasks)
            {
                if (task.AcaExecutionName == null) continue;

                try
                {
                    var status = await _dispatcher.GetExecutionStatusAsync(
                        task.ContainerJobName, task.AcaExecutionName);

                    if (status is "Succeeded" or "Failed")
                    {
                        task.Status = status == "Succeeded" ? "succeeded" : "failed";
                        task.CompletedAt = DateTimeOffset.UtcNow;
                        if (status == "Failed")
                            task.ErrorMessage = $"ACA Job execution {task.AcaExecutionName} failed";

                        _logger.LogInformation("Task {Container}/{Tenant} for run {RunId}: {Status}",
                            task.ContainerType, task.TenantKey, runId, task.Status);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error checking {Container}/{Tenant} for run {RunId}",
                        task.ContainerType, task.TenantKey, runId);
                }
            }

            // If all tasks resolved, mark run complete
            if (run.Tasks.All(t => t.Status != "dispatched"))
            {
                completedRunIds.Add(runId);
            }
        }

        // Finalize completed runs
        foreach (var runId in completedRunIds)
        {
            if (!_activeRuns.TryRemove(runId, out var run)) continue;

            var hasFailures = run.Tasks.Any(t => t.Status is "failed" or "timeout" or "dispatch_failed");
            var allFailed = run.Tasks.All(t => t.Status is "failed" or "timeout" or "dispatch_failed");
            var finalStatus = allFailed ? "failed"
                : hasFailures ? "completed_with_errors"
                : "completed";

            _logger.LogInformation("Run {RunId} for job {Job} finished: {Status}",
                runId, run.JobName, finalStatus);

            await _historyWriter.WriteRunAsync(new RunRecord(
                run.RunId, run.JobName, run.TriggerType, run.TriggeredBy, finalStatus,
                run.StartedAt, DateTimeOffset.UtcNow,
                run.ResolvedTenants.Count, run.ResolvedEntities.Count,
                run.ResolvedEntities, run.ResolvedTenants));

            var taskRecords = run.Tasks.Select(t => new TaskRecord(
                run.RunId, run.JobName, t.TenantKey, t.ContainerType,
                t.AcaExecutionName, t.Entities, t.Status,
                run.StartedAt, t.CompletedAt, t.ErrorMessage)).ToList();

            await _historyWriter.WriteTasksAsync(run.RunId, taskRecords);
        }
    }
}
