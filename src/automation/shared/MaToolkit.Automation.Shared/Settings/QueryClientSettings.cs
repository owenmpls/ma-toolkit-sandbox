using System.ComponentModel.DataAnnotations;

namespace MaToolkit.Automation.Shared.Settings;

public class QueryClientSettings
{
    public const string SectionName = "QueryClient";

    [Range(1, int.MaxValue)]
    public int CommandTimeoutSeconds { get; set; } = 120;

    [Range(1, int.MaxValue)]
    public int DatabricksWaitTimeoutSeconds { get; set; } = 120;

    [Range(1, int.MaxValue)]
    public int DatabricksPollingTimeoutMinutes { get; set; } = 5;
}
