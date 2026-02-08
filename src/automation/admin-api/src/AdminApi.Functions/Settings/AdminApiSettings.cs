using System.ComponentModel.DataAnnotations;

namespace AdminApi.Functions.Settings;

public class AdminApiSettings
{
    public const string SectionName = "AdminApi";

    [Required]
    public string SqlConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Service Bus namespace FQDN (e.g., matoolkit-sb.servicebus.windows.net).
    /// Required - used to dispatch phase/init events to the orchestrator.
    /// </summary>
    [Required]
    public string ServiceBusNamespace { get; set; } = string.Empty;
}
