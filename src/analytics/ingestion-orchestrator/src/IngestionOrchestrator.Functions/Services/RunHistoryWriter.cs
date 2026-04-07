using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using IngestionOrchestrator.Functions.Models;
using Microsoft.Extensions.Logging;

namespace IngestionOrchestrator.Functions.Services;

public interface IRunHistoryWriter
{
    Task WriteRunAsync(RunRecord run);
    Task WriteTasksAsync(string runId, IReadOnlyList<TaskRecord> tasks);
}

public class RunHistoryWriter : IRunHistoryWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly BlobContainerClient _container;
    private readonly ILogger<RunHistoryWriter> _logger;

    public RunHistoryWriter(IConfigLoader configLoader, ILogger<RunHistoryWriter> logger)
    {
        _logger = logger;
        var storage = configLoader.Storage;
        var blobServiceClient = new BlobServiceClient(
            new Uri(storage.AccountUrl.Replace(".dfs.", ".blob.")),
            new DefaultAzureCredential());
        _container = blobServiceClient.GetBlobContainerClient(storage.Container);
    }

    public async Task WriteRunAsync(RunRecord run)
    {
        try
        {
            var date = run.StartedAt.UtcDateTime.ToString("yyyy-MM-dd");
            var blobPath = $"_orchestrator/runs/{date}/run_{run.RunId}.jsonl";
            var json = JsonSerializer.Serialize(run, JsonOptions);
            await UploadAsync(blobPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write run history for {RunId} (non-fatal)", run.RunId);
        }
    }

    public async Task WriteTasksAsync(string runId, IReadOnlyList<TaskRecord> tasks)
    {
        try
        {
            if (tasks.Count == 0) return;

            var date = tasks[0].StartedAt?.UtcDateTime.ToString("yyyy-MM-dd")
                ?? DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            var blobPath = $"_orchestrator/tasks/{date}/tasks_{runId}.jsonl";

            var sb = new StringBuilder();
            foreach (var task in tasks)
                sb.AppendLine(JsonSerializer.Serialize(task, JsonOptions));

            await UploadAsync(blobPath, sb.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write task history for {RunId} (non-fatal)", runId);
        }
    }

    private async Task UploadAsync(string blobPath, string content)
    {
        var blob = _container.GetBlobClient(blobPath);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await blob.UploadAsync(stream, overwrite: true);
    }
}
