namespace Scheduler.Functions.Settings;

public class SchedulerSettings
{
    public const string SectionName = "Scheduler";

    public string SqlConnectionString { get; set; } = string.Empty;
    public string ServiceBusNamespace { get; set; } = string.Empty;
    public string OrchestratorTopicName { get; set; } = "orchestrator-events";
}
