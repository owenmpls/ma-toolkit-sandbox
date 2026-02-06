using System.Text.Json.Serialization;

namespace Orchestrator.Functions.Models.Messages;

/// <summary>
/// Message sent to the worker-jobs topic for dispatch to cloud workers.
/// </summary>
public class WorkerJobMessage
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("batchId")]
    public int BatchId { get; set; }

    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = string.Empty;

    [JsonPropertyName("functionName")]
    public string FunctionName { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public Dictionary<string, string> Parameters { get; set; } = new();

    /// <summary>
    /// Correlation data to track which execution this job belongs to.
    /// </summary>
    [JsonPropertyName("correlationData")]
    public JobCorrelationData? CorrelationData { get; set; }
}

/// <summary>
/// Correlation data embedded in the job message to enable result routing.
/// </summary>
public class JobCorrelationData
{
    [JsonPropertyName("stepExecutionId")]
    public int? StepExecutionId { get; set; }

    [JsonPropertyName("initExecutionId")]
    public int? InitExecutionId { get; set; }

    [JsonPropertyName("isInitStep")]
    public bool IsInitStep { get; set; }

    [JsonPropertyName("runbookName")]
    public string RunbookName { get; set; } = string.Empty;

    [JsonPropertyName("runbookVersion")]
    public int RunbookVersion { get; set; }
}
