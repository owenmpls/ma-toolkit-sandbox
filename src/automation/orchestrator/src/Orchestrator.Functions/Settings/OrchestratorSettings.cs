using System.ComponentModel.DataAnnotations;

namespace Orchestrator.Functions.Settings;

public class OrchestratorSettings
{
    public const string SectionName = "Orchestrator";

    [Required]
    public string SqlConnectionString { get; set; } = string.Empty;

    [Required]
    public string ServiceBusNamespace { get; set; } = string.Empty;

    [Required]
    public string OrchestratorEventsTopicName { get; set; } = "orchestrator-events";

    [Required]
    public string OrchestratorSubscriptionName { get; set; } = "orchestrator";

    [Required]
    public string WorkerJobsTopicName { get; set; } = "worker-jobs";

    [Required]
    public string WorkerResultsTopicName { get; set; } = "worker-results";

    [Required]
    public string WorkerResultsSubscriptionName { get; set; } = "orchestrator";
}
