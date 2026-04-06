using IngestionOrchestrator.Functions.Models;

namespace IngestionOrchestrator.Functions.Services;

public interface ITenantResolver
{
    IReadOnlyList<TenantConfig> Resolve(TenantSelector selector);
}

public class TenantResolver : ITenantResolver
{
    private readonly IReadOnlyList<TenantConfig> _tenants;

    public TenantResolver(IConfigLoader configLoader)
    {
        _tenants = configLoader.Tenants.Tenants;
    }

    public IReadOnlyList<TenantConfig> Resolve(TenantSelector selector)
    {
        var enabled = _tenants.Where(t => t.Enabled).ToList();

        return selector.Mode switch
        {
            "all" => enabled,
            "specific" => enabled
                .Where(t => selector.TenantKeys?.Contains(t.TenantKey) == true)
                .ToList(),
            "all_except" => enabled
                .Where(t => selector.ExcludeKeys?.Contains(t.TenantKey) != true)
                .ToList(),
            _ => throw new ArgumentException($"Unknown tenant selector mode: '{selector.Mode}'")
        };
    }
}
