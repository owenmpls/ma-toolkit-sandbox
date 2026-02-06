namespace RunbookApi.Functions.Settings;

public class RunbookApiSettings
{
    public const string SectionName = "RunbookApi";

    public string SqlConnectionString { get; set; } = string.Empty;
}
