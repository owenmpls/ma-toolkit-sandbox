using IngestionOrchestrator.Functions.Models;
using IngestionOrchestrator.Functions.Services;
using NSubstitute;

namespace IngestionOrchestrator.Functions.Tests;

public class TenantResolverTests
{
    private static readonly IReadOnlyList<TenantConfig> SampleTenants =
    [
        new("madev1", "tid-1", "org1.onmicrosoft.com", "cid-1", "cert-1", null, true, 5, 8),
        new("madev2", "tid-2", "org2.onmicrosoft.com", "cid-2", "cert-2", null, true, 10, 8),
        new("madev3", "tid-3", "org3.onmicrosoft.com", "cid-3", "cert-3", null, false, 5, 8),
    ];

    private static ITenantResolver CreateResolver()
    {
        var configLoader = Substitute.For<IConfigLoader>();
        configLoader.Tenants.Returns(new TenantsConfig(SampleTenants));
        return new TenantResolver(configLoader);
    }

    [Fact]
    public void Resolve_ModeAll_ReturnsAllEnabled()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve(new TenantSelector("all"));

        Assert.Equal(2, result.Count);
        Assert.Equal(["madev1", "madev2"], result.Select(t => t.TenantKey).ToList());
    }

    [Fact]
    public void Resolve_ModeAll_ExcludesDisabled()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve(new TenantSelector("all"));

        Assert.DoesNotContain(result, t => t.TenantKey == "madev3");
    }

    [Fact]
    public void Resolve_ModeSpecific_ReturnsOnlyNamed()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve(new TenantSelector("specific", TenantKeys: ["madev1"]));

        Assert.Single(result);
        Assert.Equal("madev1", result[0].TenantKey);
    }

    [Fact]
    public void Resolve_ModeSpecific_SkipsDisabled()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve(new TenantSelector("specific", TenantKeys: ["madev3"]));

        Assert.Empty(result);
    }

    [Fact]
    public void Resolve_ModeAllExcept_ExcludesNamed()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve(new TenantSelector("all_except", ExcludeKeys: ["madev2"]));

        Assert.Single(result);
        Assert.Equal("madev1", result[0].TenantKey);
    }

    [Fact]
    public void Resolve_UnknownMode_Throws()
    {
        var resolver = CreateResolver();

        Assert.Throws<ArgumentException>(() =>
            resolver.Resolve(new TenantSelector("invalid")));
    }
}
