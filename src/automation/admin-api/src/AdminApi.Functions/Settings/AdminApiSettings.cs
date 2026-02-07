using System.ComponentModel.DataAnnotations;

namespace AdminApi.Functions.Settings;

public class AdminApiSettings
{
    public const string SectionName = "AdminApi";

    [Required]
    public string SqlConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Service Bus namespace FQDN (e.g., matoolkit-sb.servicebus.windows.net).
    /// Optional - if not set, manual batch advancement won't dispatch events.
    /// </summary>
    public string? ServiceBusNamespace { get; set; }
}
