using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using IngestionOrchestrator.Functions.Models;
using IngestionOrchestrator.Functions.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IngestionOrchestrator.Functions.Services;

public interface IRunTracker
{
    Task TrackAsync(TrackedRun run);
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
    public required string ContainerJobName { get; set; }
    public required string TenantKey { get; set; }
    public required string ContainerType { get; set; }
    public required IReadOnlyList<string> Entities { get; set; }
    public string? AcaExecutionName { get; set; }
    public string Status { get; set; } = "dispatched";
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

public class RunTracker : IRunTracker
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromHours(8);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private const string TrackingPrefix = "_orchestrator/tracking/";

    private readonly BlobContainerClient _container;
    private readonly IContainerJobDispatcher _dispatcher;
    private readonly IRunHistoryWriter _historyWriter;
    private readonly ILogger<RunTracker> _logger;

    public RunTracker(
        IContainerJobDispatcher dispatcher,
        IRunHistoryWriter historyWriter,
        IConfigLoader configLoader,
        ILogger<RunTracker> logger)
    {
        _dispatcher = dispatcher;
        _historyWriter = historyWriter;
        _logger = logger;

        var storage = configLoader.Storage;
        var blobServiceClient = new BlobServiceClient(
            new Uri(storage.AccountUrl.Replace(".dfs.", ".blob.")),
            new DefaultAzureCredential());
        _container = blobServiceClient.GetBlobContainerClient(storage.Container);
    }

    public async Task TrackAsync(TrackedRun run)
    {
        var blobPath = $"{TrackingPrefix}{run.RunId}.json";
        var json = JsonSerializer.Serialize(run, JsonOptions);
        var blob = _container.GetBlobClient(blobPath);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        await blob.UploadAsync(stream, overwrite: true);
        _logger.LogInformation("Tracking run {RunId} ({TaskCount} tasks)", run.RunId, run.Tasks.Count);
    }

    public async Task CheckActiveRunsAsync()
    {
        // List all tracking blobs
        var runs = new List<(string BlobPath, TrackedRun Run)>();
        await foreach (var blobItem in _container.GetBlobsAsync(prefix: TrackingPrefix))
        {
            try
            {
                var blob = _container.GetBlobClient(blobItem.Name);
                var response = await blob.DownloadContentAsync();
                var run = JsonSerializer.Deserialize<TrackedRun>(
                    response.Value.Content.ToString(), JsonOptions);
                if (run != null)
                    runs.Add((blobItem.Name, run));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read tracking blob {Blob}", blobItem.Name);
            }
        }

        if (runs.Count == 0) return;

        _logger.LogInformation("Checking {Count} active run(s)", runs.Count);

        foreach (var (blobPath, run) in runs)
        {
            var changed = false;

            // Check for timeout
            if (DateTimeOffset.UtcNow - run.StartedAt > RunTimeout)
            {
                _logger.LogWarning("Run {RunId} timed out after {Hours}h", run.RunId, RunTimeout.TotalHours);
                foreach (var task in run.Tasks.Where(t => t.Status == "dispatched"))
                {
                    task.Status = "timeout";
                    task.CompletedAt = DateTimeOffset.UtcNow;
                    task.ErrorMessage = "Run timed out";
                    changed = true;
                }
            }
            else
            {
                // Check each pending task
                foreach (var task in run.Tasks.Where(t => t.Status == "dispatched"))
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
                            changed = true;

                            _logger.LogInformation("Task {Container}/{Tenant} for run {RunId}: {Status}",
                                task.ContainerType, task.TenantKey, run.RunId, task.Status);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error checking {Container}/{Tenant} for run {RunId}",
                            task.ContainerType, task.TenantKey, run.RunId);
                    }
                }
            }

            // If all tasks resolved, finalize and delete tracking blob
            if (run.Tasks.All(t => t.Status != "dispatched"))
            {
                var hasFailures = run.Tasks.Any(t => t.Status is "failed" or "timeout" or "dispatch_failed");
                var allFailed = run.Tasks.All(t => t.Status is "failed" or "timeout" or "dispatch_failed");
                var finalStatus = allFailed ? "failed"
                    : hasFailures ? "completed_with_errors"
                    : "completed";

                _logger.LogInformation("Run {RunId} for job {Job} finished: {Status}",
                    run.RunId, run.JobName, finalStatus);

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

                // Delete tracking blob
                try
                {
                    await _container.GetBlobClient(blobPath).DeleteIfExistsAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete tracking blob {Blob}", blobPath);
                }
            }
            else if (changed)
            {
                // Update tracking blob with partial progress
                try
                {
                    var json = JsonSerializer.Serialize(run, JsonOptions);
                    var blob = _container.GetBlobClient(blobPath);
                    using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
                    await blob.UploadAsync(stream, overwrite: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update tracking blob {Blob}", blobPath);
                }
            }
        }
    }
}
