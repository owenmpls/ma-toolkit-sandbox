using IngestionOrchestrator.Functions.Models;
using IngestionOrchestrator.Functions.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace IngestionOrchestrator.Functions.Tests;

public class RunExecutorOverlapTests
{
    private static readonly IReadOnlyList<EntityType> SampleEntities =
    [
        new("entra_users", 1, "graph-ingest"),
    ];

    private static readonly IReadOnlyList<TenantConfig> SampleTenants =
    [
        new("madev1", "tid-1", "org1.onmicrosoft.com", "cid-1", "cert-1", null, true, 5, 8),
    ];

    private static readonly StorageConfig SampleStorage = new(
        "https://test.dfs.core.windows.net", "landing",
        new StorageAuthConfig("managed_identity"));

    private static readonly JobDefinition SampleJob = new(
        "daily-full", "Full daily ingestion", "0 0 * * *", true,
        new EntitySelector(IncludeTiers: [1]),
        new TenantSelector("all"));

    private readonly IRunTracker _runTracker = Substitute.For<IRunTracker>();
    private readonly IContainerJobDispatcher _dispatcher = Substitute.For<IContainerJobDispatcher>();
    private readonly IRunExecutor _executor;

    public RunExecutorOverlapTests()
    {
        var configLoader = Substitute.For<IConfigLoader>();
        configLoader.EntityRegistry.Returns(new EntityRegistryConfig(SampleEntities));
        configLoader.Tenants.Returns(new TenantsConfig(SampleTenants));
        configLoader.Storage.Returns(SampleStorage);

        var entityResolver = new EntityResolver(configLoader);
        var tenantResolver = new TenantResolver(configLoader);

        _dispatcher.StartJobAsync(Arg.Any<string>(), Arg.Any<TenantConfig>(),
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<StorageConfig>())
            .Returns("exec-001");

        _executor = new RunExecutor(
            entityResolver, tenantResolver, _dispatcher, _runTracker,
            configLoader, Substitute.For<ILogger<RunExecutor>>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenJobIsActive_SkipsDispatch()
    {
        _runTracker.IsJobActiveAsync("daily-full").Returns(true);

        var result = await _executor.ExecuteAsync(SampleJob, "scheduled", null);

        Assert.False(result);
        await _dispatcher.DidNotReceive().StartJobAsync(
            Arg.Any<string>(), Arg.Any<TenantConfig>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<StorageConfig>());
        await _runTracker.DidNotReceive().TrackAsync(Arg.Any<TrackedRun>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoActiveJob_Dispatches()
    {
        _runTracker.IsJobActiveAsync("daily-full").Returns(false);

        var result = await _executor.ExecuteAsync(SampleJob, "scheduled", null);

        Assert.True(result);
        await _dispatcher.Received(1).StartJobAsync(
            "graph-ingest", Arg.Any<TenantConfig>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<StorageConfig>());
        await _runTracker.Received(1).TrackAsync(Arg.Any<TrackedRun>());
    }

    [Fact]
    public async Task ExecuteAsync_WithForce_SkipsOverlapCheck()
    {
        _runTracker.IsJobActiveAsync("daily-full").Returns(true);

        var result = await _executor.ExecuteAsync(SampleJob, "manual", "user@test.com",
            force: true);

        Assert.True(result);
        await _runTracker.DidNotReceive().IsJobActiveAsync(Arg.Any<string>());
        await _dispatcher.Received(1).StartJobAsync(
            "graph-ingest", Arg.Any<TenantConfig>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<StorageConfig>());
    }
}
