using IngestionOrchestrator.Functions.Models;
using IngestionOrchestrator.Functions.Services;
using NSubstitute;

namespace IngestionOrchestrator.Functions.Tests;

public class EntityResolverTests
{
    private static readonly IReadOnlyList<EntityType> SampleEntities =
    [
        new("entra_users", 1, "graph-ingest"),
        new("entra_groups", 1, "graph-ingest"),
        new("exo_mailboxes", 1, "exo-ingest"),
        new("spo_sites", 1, "spo-ingest"),
        new("entra_group_members", 2, "graph-ingest"),
        new("entra_group_owners", 2, "graph-ingest"),
        new("exo_group_members", 2, "exo-ingest"),
        new("exo_mailbox_statistics", 3, "exo-ingest"),
        new("spo_site_permissions", 3, "spo-ingest"),
    ];

    private static IEntityResolver CreateResolver()
    {
        var configLoader = Substitute.For<IConfigLoader>();
        configLoader.EntityRegistry.Returns(new EntityRegistryConfig(SampleEntities));
        return new EntityResolver(configLoader);
    }

    [Fact]
    public void Resolve_IncludeTier1_ReturnsAllTier1Entities()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve(new EntitySelector(IncludeTiers: [1]));

        Assert.Equal(4, result.Count);
        Assert.Equal(["entra_users", "entra_groups", "exo_mailboxes", "spo_sites"],
            result.Select(e => e.Name).ToList());
    }

    [Fact]
    public void Resolve_IncludeMultipleTiers_ReturnsUnion()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve(new EntitySelector(IncludeTiers: [1, 2]));

        Assert.Equal(7, result.Count);
        Assert.Contains(result, e => e.Name == "entra_users");
        Assert.Contains(result, e => e.Name == "entra_group_members");
    }

    [Fact]
    public void Resolve_TierPlusIncludeEntities_AddsSpecificEntity()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve(new EntitySelector(
            IncludeTiers: [1],
            IncludeEntities: ["entra_group_members"]));

        Assert.Equal(5, result.Count);
        Assert.Contains(result, e => e.Name == "entra_users");
        Assert.Contains(result, e => e.Name == "entra_group_members");
    }

    [Fact]
    public void Resolve_TierPlusExcludeEntities_RemovesSpecificEntity()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve(new EntitySelector(
            IncludeTiers: [3],
            ExcludeEntities: ["exo_mailbox_statistics"]));

        Assert.Single(result);
        Assert.Equal("spo_site_permissions", result[0].Name);
    }

    [Fact]
    public void Resolve_OnlyIncludeEntities_ReturnsExactEntities()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve(new EntitySelector(
            IncludeEntities: ["entra_groups", "entra_group_members"]));

        Assert.Equal(2, result.Count);
        Assert.Equal("entra_groups", result[0].Name);
        Assert.Equal("entra_group_members", result[1].Name);
    }

    [Fact]
    public void Resolve_EmptySelector_ReturnsEmpty()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve(new EntitySelector());

        Assert.Empty(result);
    }

    [Fact]
    public void Resolve_IncludeAlreadyInTier_NoDuplicates()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve(new EntitySelector(
            IncludeTiers: [1],
            IncludeEntities: ["entra_users"]));

        Assert.Equal(4, result.Count);
        Assert.Single(result.Where(e => e.Name == "entra_users"));
    }

    [Fact]
    public void Resolve_PreservesRegistryOrder()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve(new EntitySelector(
            IncludeEntities: ["spo_site_permissions", "entra_users"]));

        // Should follow registry order (entra_users first), not input order
        Assert.Equal("entra_users", result[0].Name);
        Assert.Equal("spo_site_permissions", result[1].Name);
    }

    [Fact]
    public void Resolve_GroupByContainer_ProducesCorrectGroups()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve(new EntitySelector(IncludeTiers: [1, 2]));

        var groups = result.GroupBy(e => e.Container)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Name).ToList());

        Assert.Equal(3, groups.Count);
        Assert.Contains("entra_users", groups["graph-ingest"]);
        Assert.Contains("entra_group_members", groups["graph-ingest"]);
        Assert.Contains("exo_mailboxes", groups["exo-ingest"]);
        Assert.Contains("spo_sites", groups["spo-ingest"]);
    }
}
