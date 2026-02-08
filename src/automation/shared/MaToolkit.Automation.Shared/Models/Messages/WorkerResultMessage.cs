using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaToolkit.Automation.Shared.Models.Messages;

/// <summary>
/// Message received from the worker-results topic containing job execution results.
/// </summary>
public class WorkerResultMessage
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty; // "Success" or "Failure"

    [JsonPropertyName("resultType")]
    public string? ResultType { get; set; } // "Boolean", "Object", etc.

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public WorkerErrorInfo? Error { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Correlation data echoed back from the job message.
    /// </summary>
    [JsonPropertyName("correlationData")]
    public JobCorrelationData? CorrelationData { get; set; }

    /// <summary>
    /// Check if this is a polling response that indicates the step is still in progress.
    /// Convention: { "complete": false } means still polling.
    /// </summary>
    public bool IsPollingInProgress()
    {
        if (Status != "Success" || Result is null)
            return false;

        try
        {
            if (TryGetPropertyCaseInsensitive(Result.Value, "complete", out var complete))
            {
                return !complete.GetBoolean();
            }
        }
        catch (InvalidOperationException)
        {
            // Not a polling result format
        }

        return false;
    }

    /// <summary>
    /// Extract the final data from a completed polling result.
    /// Convention: { "complete": true, "data": {...} }
    /// </summary>
    public JsonElement? GetPollingResultData()
    {
        if (Result is null)
            return null;

        try
        {
            if (TryGetPropertyCaseInsensitive(Result.Value, "data", out var data))
            {
                return data;
            }
        }
        catch (InvalidOperationException)
        {
            // Not a polling result format
        }

        return Result;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}

/// <summary>
/// Error information from a failed worker job.
/// </summary>
public class WorkerErrorInfo
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("isThrottled")]
    public bool IsThrottled { get; set; }

    [JsonPropertyName("attempts")]
    public int Attempts { get; set; }

    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; set; }
}
