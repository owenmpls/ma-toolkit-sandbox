using System.ComponentModel.DataAnnotations;

namespace Scheduler.Functions.Settings;

public class SchedulerSettings
{
    public const string SectionName = "Scheduler";

    [Required]
    public string SqlConnectionString { get; set; } = string.Empty;

    [Required]
    public string ServiceBusNamespace { get; set; } = string.Empty;

    [Required]
    public string OrchestratorTopicName { get; set; } = "orchestrator-events";
}
