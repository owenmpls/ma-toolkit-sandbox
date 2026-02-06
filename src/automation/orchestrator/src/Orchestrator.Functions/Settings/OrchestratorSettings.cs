namespace Orchestrator.Functions.Settings;

public class OrchestratorSettings
{
    public const string SectionName = "Orchestrator";

    public string SqlConnectionString { get; set; } = string.Empty;
    public string ServiceBusNamespace { get; set; } = string.Empty;
    public string OrchestratorEventsTopicName { get; set; } = "orchestrator-events";
    public string OrchestratorSubscriptionName { get; set; } = "orchestrator";
    public string WorkerJobsTopicName { get; set; } = "worker-jobs";
    public string WorkerResultsTopicName { get; set; } = "worker-results";
    public string WorkerResultsSubscriptionName { get; set; } = "orchestrator";
}
