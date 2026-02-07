namespace MaToolkit.Automation.Shared.Settings;

public class QueryClientSettings
{
    public const string SectionName = "QueryClient";

    public int CommandTimeoutSeconds { get; set; } = 120;
    public int DatabricksWaitTimeoutSeconds { get; set; } = 120;
    public int DatabricksPollingTimeoutMinutes { get; set; } = 5;
}
