using System.Data;
using FluentAssertions;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Moq;
using Scheduler.Functions.Functions;
using Scheduler.Functions.Services;
using Xunit;

namespace Scheduler.Functions.Tests.Functions;

public class SchedulerTimerFunctionTests
{
    private readonly Mock<IRunbookRepository> _runbookRepo;
    private readonly Mock<IAutomationSettingsRepository> _automationRepo;
    private readonly Mock<IBatchRepository> _batchRepo;
    private readonly Mock<IDataSourceQueryService> _dataSourceQuery;
    private readonly Mock<IRunbookParser> _parser;
    private readonly Mock<IBatchDetector> _batchDetector;
    private readonly Mock<IPhaseDispatcher> _phaseDispatcher;
    private readonly Mock<IVersionTransitionHandler> _versionTransitionHandler;
    private readonly Mock<IPollingManager> _pollingManager;
    private readonly Mock<IDistributedLock> _distributedLock;
    private readonly Mock<ILogger<SchedulerTimerFunction>> _logger;
    private readonly SchedulerTimerFunction _sut;

    public SchedulerTimerFunctionTests()
    {
        _runbookRepo = new Mock<IRunbookRepository>();
        _automationRepo = new Mock<IAutomationSettingsRepository>();
        _batchRepo = new Mock<IBatchRepository>();
        _dataSourceQuery = new Mock<IDataSourceQueryService>();
        _parser = new Mock<IRunbookParser>();
        _batchDetector = new Mock<IBatchDetector>();
        _phaseDispatcher = new Mock<IPhaseDispatcher>();
        _versionTransitionHandler = new Mock<IVersionTransitionHandler>();
        _pollingManager = new Mock<IPollingManager>();
        _distributedLock = new Mock<IDistributedLock>();
        _logger = new Mock<ILogger<SchedulerTimerFunction>>();

        // Default: lock acquired
        var lockHandle = new Mock<IAsyncDisposable>();
        _distributedLock.Setup(x => x.TryAcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lockHandle.Object);

        _sut = new SchedulerTimerFunction(
            _runbookRepo.Object, _automationRepo.Object, _batchRepo.Object,
            _dataSourceQuery.Object, _parser.Object, _batchDetector.Object,
            _phaseDispatcher.Object, _versionTransitionHandler.Object,
            _pollingManager.Object, _distributedLock.Object, _logger.Object);
    }

    [Fact]
    public async Task RunAsync_LockNotAcquired_SkipsProcessing()
    {
        _distributedLock.Setup(x => x.TryAcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAsyncDisposable?)null);

        await _sut.RunAsync(CreateTimerInfo());

        _runbookRepo.Verify(x => x.GetActiveRunbooksAsync(), Times.Never);
    }

    [Fact]
    public async Task RunAsync_NoRunbooks_CompletesGracefully()
    {
        _runbookRepo.Setup(x => x.GetActiveRunbooksAsync()).ReturnsAsync(new List<RunbookRecord>());

        await _sut.RunAsync(CreateTimerInfo());

        _pollingManager.Verify(x => x.CheckPollingStepsAsync(It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_AutomationEnabled_ExecutesQueryAndDetectsBatches()
    {
        var runbook = new RunbookRecord { Id = 1, Name = "test", Version = 1, YamlContent = "yaml" };
        var definition = new RunbookDefinition { Name = "test", DataSource = new DataSourceConfig { PrimaryKey = "id" } };
        var queryResults = new DataTable();
        queryResults.Columns.Add("id");
        queryResults.Rows.Add("member1");

        _runbookRepo.Setup(x => x.GetActiveRunbooksAsync()).ReturnsAsync(new[] { runbook });
        _parser.Setup(x => x.Parse("yaml")).Returns(definition);
        _automationRepo.Setup(x => x.GetByNameAsync("test"))
            .ReturnsAsync(new AutomationSettingsRecord { RunbookName = "test", AutomationEnabled = true });
        _dataSourceQuery.Setup(x => x.ExecuteAsync(definition.DataSource)).ReturnsAsync(queryResults);
        _batchDetector.Setup(x => x.GroupByBatchTimeAsync(queryResults, definition.DataSource))
            .ReturnsAsync(new Dictionary<DateTime, List<DataRow>>
            {
                [DateTime.UtcNow] = queryResults.Rows.Cast<DataRow>().ToList()
            });
        _batchRepo.Setup(x => x.GetActiveByRunbookNameAsync("test")).ReturnsAsync(new List<BatchRecord>());

        await _sut.RunAsync(CreateTimerInfo());

        _dataSourceQuery.Verify(x => x.ExecuteAsync(definition.DataSource), Times.Once);
        _batchDetector.Verify(x => x.ProcessBatchGroupAsync(
            runbook, definition, It.IsAny<DateTime>(), It.IsAny<List<DataRow>>(), It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_AutomationDisabled_SkipsQueryButProcessesExistingBatches()
    {
        var runbook = new RunbookRecord { Id = 1, Name = "test", Version = 1, YamlContent = "yaml" };
        var definition = new RunbookDefinition { Name = "test", DataSource = new DataSourceConfig { PrimaryKey = "id" } };

        _runbookRepo.Setup(x => x.GetActiveRunbooksAsync()).ReturnsAsync(new[] { runbook });
        _parser.Setup(x => x.Parse("yaml")).Returns(definition);
        _automationRepo.Setup(x => x.GetByNameAsync("test"))
            .ReturnsAsync(new AutomationSettingsRecord { RunbookName = "test", AutomationEnabled = false });
        _batchRepo.Setup(x => x.GetActiveByRunbookNameAsync("test"))
            .ReturnsAsync(new[] { new BatchRecord { Id = 10, Status = "active" } });

        await _sut.RunAsync(CreateTimerInfo());

        _dataSourceQuery.Verify(x => x.ExecuteAsync(It.IsAny<DataSourceConfig>()), Times.Never);
        _phaseDispatcher.Verify(x => x.EvaluatePendingPhasesAsync(
            runbook, definition, It.IsAny<BatchRecord>(), It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_QueryFails_ContinuesWithExistingBatches()
    {
        var runbook = new RunbookRecord { Id = 1, Name = "test", Version = 1, YamlContent = "yaml" };
        var definition = new RunbookDefinition { Name = "test", DataSource = new DataSourceConfig { PrimaryKey = "id" } };

        _runbookRepo.Setup(x => x.GetActiveRunbooksAsync()).ReturnsAsync(new[] { runbook });
        _parser.Setup(x => x.Parse("yaml")).Returns(definition);
        _automationRepo.Setup(x => x.GetByNameAsync("test"))
            .ReturnsAsync(new AutomationSettingsRecord { RunbookName = "test", AutomationEnabled = true });
        _dataSourceQuery.Setup(x => x.ExecuteAsync(definition.DataSource))
            .ThrowsAsync(new InvalidOperationException("Connection failed"));
        _batchRepo.Setup(x => x.GetActiveByRunbookNameAsync("test"))
            .ReturnsAsync(new[] { new BatchRecord { Id = 10, Status = "active" } });

        await _sut.RunAsync(CreateTimerInfo());

        _phaseDispatcher.Verify(x => x.EvaluatePendingPhasesAsync(
            runbook, definition, It.IsAny<BatchRecord>(), It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_ErrorInOneRunbook_ContinuesProcessingOthers()
    {
        var runbook1 = new RunbookRecord { Id = 1, Name = "good", Version = 1, YamlContent = "yaml1" };
        var runbook2 = new RunbookRecord { Id = 2, Name = "bad", Version = 1, YamlContent = "yaml2" };
        var runbook3 = new RunbookRecord { Id = 3, Name = "also-good", Version = 1, YamlContent = "yaml3" };

        var definition = new RunbookDefinition { Name = "test", DataSource = new DataSourceConfig { PrimaryKey = "id" } };

        _runbookRepo.Setup(x => x.GetActiveRunbooksAsync()).ReturnsAsync(new[] { runbook1, runbook2, runbook3 });
        _parser.Setup(x => x.Parse("yaml1")).Returns(definition);
        _parser.Setup(x => x.Parse("yaml2")).Throws(new Exception("Bad YAML"));
        _parser.Setup(x => x.Parse("yaml3")).Returns(definition);
        _automationRepo.Setup(x => x.GetByNameAsync(It.IsAny<string>()))
            .ReturnsAsync(new AutomationSettingsRecord { AutomationEnabled = false });
        _batchRepo.Setup(x => x.GetActiveByRunbookNameAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<BatchRecord>());

        await _sut.RunAsync(CreateTimerInfo());

        // Verify error was recorded for the bad runbook
        _runbookRepo.Verify(x => x.SetLastErrorAsync(2, "Bad YAML"), Times.Once);
        // Verify good runbooks were still processed (ClearLastError called)
        _runbookRepo.Verify(x => x.ClearLastErrorAsync(1), Times.Once);
        _runbookRepo.Verify(x => x.ClearLastErrorAsync(3), Times.Once);
    }

    [Fact]
    public async Task RunAsync_NoQueryResults_SkipsBatchDetection()
    {
        var runbook = new RunbookRecord { Id = 1, Name = "test", Version = 1, YamlContent = "yaml" };
        var definition = new RunbookDefinition { Name = "test", DataSource = new DataSourceConfig { PrimaryKey = "id" } };
        var emptyResults = new DataTable();
        emptyResults.Columns.Add("id");

        _runbookRepo.Setup(x => x.GetActiveRunbooksAsync()).ReturnsAsync(new[] { runbook });
        _parser.Setup(x => x.Parse("yaml")).Returns(definition);
        _automationRepo.Setup(x => x.GetByNameAsync("test"))
            .ReturnsAsync(new AutomationSettingsRecord { RunbookName = "test", AutomationEnabled = true });
        _dataSourceQuery.Setup(x => x.ExecuteAsync(definition.DataSource)).ReturnsAsync(emptyResults);
        _batchRepo.Setup(x => x.GetActiveByRunbookNameAsync("test")).ReturnsAsync(new List<BatchRecord>());

        await _sut.RunAsync(CreateTimerInfo());

        _batchDetector.Verify(x => x.GroupByBatchTimeAsync(It.IsAny<DataTable>(), It.IsAny<DataSourceConfig>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_AlwaysChecksPollingSteps()
    {
        _runbookRepo.Setup(x => x.GetActiveRunbooksAsync()).ReturnsAsync(new List<RunbookRecord>());

        await _sut.RunAsync(CreateTimerInfo());

        _pollingManager.Verify(x => x.CheckPollingStepsAsync(It.IsAny<DateTime>()), Times.Once);
    }

    private static TimerInfo CreateTimerInfo()
    {
        return new TimerInfo();
    }
}
