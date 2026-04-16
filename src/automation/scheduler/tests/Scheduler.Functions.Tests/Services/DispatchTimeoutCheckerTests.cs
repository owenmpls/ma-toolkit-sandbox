using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;
using Scheduler.Functions.Services;
using Scheduler.Functions.Settings;
using Xunit;

namespace Scheduler.Functions.Tests.Services;

public class DispatchTimeoutCheckerTests
{
    private readonly Mock<IStepExecutionRepository> _stepRepo = new();
    private readonly Mock<IInitExecutionRepository> _initRepo = new();
    private readonly Mock<IServiceBusPublisher> _publisher = new();
    private readonly Mock<IDbConnectionFactory> _db = new();
    private readonly DispatchTimeoutChecker _sut;

    public DispatchTimeoutCheckerTests()
    {
        var settings = Options.Create(new SchedulerSettings
        {
            SqlConnectionString = "Server=test",
            ServiceBusNamespace = "test.servicebus.windows.net",
            DispatchTimeoutMinutes = 30
        });

        _sut = new DispatchTimeoutChecker(
            _stepRepo.Object,
            _initRepo.Object,
            _publisher.Object,
            _db.Object,
            settings,
            Mock.Of<ILogger<DispatchTimeoutChecker>>());
    }

    private static DbConnection CreateMockDbConnection()
    {
        var mockReader = new Mock<DbDataReader>();
        mockReader.Setup(x => x.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        mockReader.Setup(x => x.NextResultAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        mockReader.Setup(x => x.FieldCount).Returns(0);
        mockReader.Setup(x => x.HasRows).Returns(false);

        var mockParams = new Mock<DbParameterCollection>();

        var mockCmd = new Mock<DbCommand>();
        mockCmd.Protected()
            .Setup<Task<DbDataReader>>("ExecuteDbDataReaderAsync",
                ItExpr.IsAny<CommandBehavior>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockReader.Object);
        mockCmd.Protected()
            .SetupGet<DbParameterCollection>("DbParameterCollection")
            .Returns(mockParams.Object);
        mockCmd.Protected()
            .Setup<DbParameter>("CreateDbParameter")
            .Returns(new Mock<DbParameter>().Object);
        mockCmd.SetupProperty(x => x.CommandText);
        mockCmd.SetupProperty(x => x.CommandType);

        var mockConn = new Mock<DbConnection>();
        mockConn.Protected()
            .Setup<DbCommand>("CreateDbCommand")
            .Returns(mockCmd.Object);
        mockConn.Setup(x => x.State).Returns(ConnectionState.Open);
        return mockConn.Object;
    }

    [Fact]
    public async Task CheckDispatchedStepsAsync_StuckStep_PublishesTimeoutMessage()
    {
        var dispatchedAt = DateTime.UtcNow.AddMinutes(-45);
        var step = new StepExecutionRecord
        {
            Id = 42,
            StepName = "Set-Something",
            Status = StepStatus.Dispatched,
            DispatchedAt = dispatchedAt
        };
        _stepRepo.Setup(x => x.GetDispatchedStepsOlderThanAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new[] { step });
        _initRepo.Setup(x => x.GetDispatchedStepsOlderThanAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(Array.Empty<InitExecutionRecord>());

        var mockConn = CreateMockDbConnection();
        _db.Setup(x => x.CreateConnection()).Returns(mockConn);

        // when dapper returns null the publisher shouldn't be triggered
        var now = DateTime.UtcNow;
        await _sut.CheckDispatchedStepsAsync(now);

        _stepRepo.Verify(x => x.GetDispatchedStepsOlderThanAsync(
            It.Is<DateTime>(d => d <= now.AddMinutes(-29) && d >= now.AddMinutes(-31))), Times.Once);
    }

    [Fact]
    public async Task CheckDispatchedStepsAsync_StuckInit_QueriesWithCorrectCutoff()
    {
        var dispatchedAt = DateTime.UtcNow.AddMinutes(-45);
        var init = new InitExecutionRecord
        {
            Id = 7,
            BatchId = 10,
            StepName = "New-MigrationBatch",
            Status = StepStatus.Dispatched,
            DispatchedAt = dispatchedAt
        };
        _stepRepo.Setup(x => x.GetDispatchedStepsOlderThanAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(Array.Empty<StepExecutionRecord>());
        _initRepo.Setup(x => x.GetDispatchedStepsOlderThanAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new[] { init });

        var mockConn = CreateMockDbConnection();
        _db.Setup(x => x.CreateConnection()).Returns(mockConn);

        var now = DateTime.UtcNow;
        await _sut.CheckDispatchedStepsAsync(now);

        _initRepo.Verify(x => x.GetDispatchedStepsOlderThanAsync(
            It.Is<DateTime>(d => d <= now.AddMinutes(-29) && d >= now.AddMinutes(-31))), Times.Once);
    }

    [Fact]
    public async Task CheckDispatchedStepsAsync_NoStuckSteps_PublishesNothing()
    {
        _stepRepo.Setup(x => x.GetDispatchedStepsOlderThanAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(Array.Empty<StepExecutionRecord>());
        _initRepo.Setup(x => x.GetDispatchedStepsOlderThanAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(Array.Empty<InitExecutionRecord>());

        await _sut.CheckDispatchedStepsAsync(DateTime.UtcNow);

        _publisher.Verify(x => x.PublishDispatchTimeoutAsync(It.IsAny<DispatchTimeoutMessage>()), Times.Never);
        _db.Verify(x => x.CreateConnection(), Times.Never);
    }

    [Fact]
    public async Task CheckDispatchedStepsAsync_BatchInfoNotFound_SkipsStep()
    {
        var step = new StepExecutionRecord
        {
            Id = 42,
            StepName = "Set-Something",
            Status = StepStatus.Dispatched,
            DispatchedAt = DateTime.UtcNow.AddMinutes(-45)
        };
        _stepRepo.Setup(x => x.GetDispatchedStepsOlderThanAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new[] { step });
        _initRepo.Setup(x => x.GetDispatchedStepsOlderThanAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(Array.Empty<InitExecutionRecord>());

        // mock returns no rows (batchInfo will be null)
        var mockConn = CreateMockDbConnection();
        _db.Setup(x => x.CreateConnection()).Returns(mockConn);

        await _sut.CheckDispatchedStepsAsync(DateTime.UtcNow);

        // publisher shouldn't be called because batchInfo null
        _publisher.Verify(x => x.PublishDispatchTimeoutAsync(It.IsAny<DispatchTimeoutMessage>()), Times.Never);
    }

    [Fact]
    public async Task CheckDispatchedStepsAsync_UsesConfiguredTimeout()
    {
        var customSettings = Options.Create(new SchedulerSettings
        {
            SqlConnectionString = "Server=test",
            ServiceBusNamespace = "test.servicebus.windows.net",
            DispatchTimeoutMinutes = 60
        });

        var sut = new DispatchTimeoutChecker(
            _stepRepo.Object, _initRepo.Object, _publisher.Object,
            _db.Object, customSettings,
            Mock.Of<ILogger<DispatchTimeoutChecker>>());

        _stepRepo.Setup(x => x.GetDispatchedStepsOlderThanAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(Array.Empty<StepExecutionRecord>());
        _initRepo.Setup(x => x.GetDispatchedStepsOlderThanAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(Array.Empty<InitExecutionRecord>());

        var now = DateTime.UtcNow;
        await sut.CheckDispatchedStepsAsync(now);

        // 60 min timeout; cutoff should be approx 60 minutes ago
        _stepRepo.Verify(x => x.GetDispatchedStepsOlderThanAsync(
            It.Is<DateTime>(d => d <= now.AddMinutes(-59) && d >= now.AddMinutes(-61))), Times.Once);
    }
}