using System.ComponentModel.DataAnnotations;

namespace IngestionOrchestrator.Functions.Settings;

public class IngestionSettings
{
    public const string SectionName = "Ingestion";

    [Required]
    public required string KeyVaultName { get; set; }

    [Required]
    public required string SubscriptionId { get; set; }

    [Required]
    public required string ResourceGroupName { get; set; }

    public string ConfigPath { get; set; } = "Config";
}
