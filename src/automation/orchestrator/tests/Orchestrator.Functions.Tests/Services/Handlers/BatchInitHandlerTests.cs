using System.Data;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;
using Orchestrator.Functions.Services;
using Orchestrator.Functions.Services.Handlers;
using Xunit;

namespace Orchestrator.Functions.Tests.Services.Handlers;

public class BatchInitHandlerTests
{
    private readonly Mock<IBatchRepository> _batchRepo = new();
    private readonly Mock<IRunbookRepository> _runbookRepo = new();
    private readonly Mock<IInitExecutionRepository> _initRepo = new();
    private readonly Mock<IWorkerDispatcher> _workerDispatcher = new();
    private readonly Mock<IRunbookParser> _runbookParser = new();
    private readonly Mock<ITemplateResolver> _templateResolver = new();
    private readonly Mock<IPhaseEvaluator> _phaseEvaluator = new();
    private readonly Mock<IDbConnectionFactory> _db = new();
    private readonly BatchInitHandler _sut;

    public BatchInitHandlerTests()
    {
        _workerDispatcher.Setup(x => x.DispatchJobAsync(It.IsAny<WorkerJobMessage>()))
            .ReturnsAsync("job-id");
        _initRepo.Setup(x => x.SetDispatchedAsync(It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _initRepo.Setup(x => x.InsertAsync(It.IsAny<InitExecutionRecord>(), It.IsAny<IDbTransaction>()))
            .ReturnsAsync(1);

        // Setup mock DB connection + transaction for creation path
        var mockTransaction = new Mock<IDbTransaction>();
        var mockConnection = new Mock<IDbConnection>();
        mockConnection.Setup(c => c.BeginTransaction()).Returns(mockTransaction.Object);
        _db.Setup(d => d.CreateConnection()).Returns(mockConnection.Object);

        _sut = new BatchInitHandler(
            _batchRepo.Object,
            _runbookRepo.Object,
            _initRepo.Object,
            _workerDispatcher.Object,
            _runbookParser.Object,
            _templateResolver.Object,
            _phaseEvaluator.Object,
            _db.Object,
            Mock.Of<ILogger<BatchInitHandler>>());
    }

    [Fact]
    public async Task HandleAsync_CreatesInitExecutions_WhenNoneExist()
    {
        var message = CreateMessage(batchId: 1, runbookName: "test", version: 1);
        SetupBatchAndRunbook(message, initStepCount: 2);

        // No existing init executions
        _initRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(new List<InitExecutionRecord>());

        // After creation, return them as pending
        _initRepo.Setup(x => x.GetPendingByBatchAsync(1)).ReturnsAsync(new List<InitExecutionRecord>
        {
            Init(1, batchId: 1, index: 0, status: StepStatus.Pending, workerId: "w1", functionName: "fn0"),
            Init(2, batchId: 1, index: 1, status: StepStatus.Pending, workerId: "w1", functionName: "fn1")
        });

        await _sut.HandleAsync(message);

        // Should insert 2 init executions
        _initRepo.Verify(x => x.InsertAsync(It.IsAny<InitExecutionRecord>(), It.IsAny<IDbTransaction>()),
            Times.Exactly(2));
        // Should dispatch first step
        _workerDispatcher.Verify(x => x.DispatchJobAsync(It.Is<WorkerJobMessage>(j => j.JobId == "init-1")),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_SkipsCreation_WhenVersionAlreadyExists()
    {
        var message = CreateMessage(batchId: 1, runbookName: "test", version: 2);
        SetupBatchAndRunbook(message, initStepCount: 1);

        // Existing init executions for version 2 already present
        _initRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(new List<InitExecutionRecord>
        {
            Init(1, batchId: 1, index: 0, status: StepStatus.Pending, workerId: "w1", functionName: "fn0", runbookVersion: 2)
        });

        _initRepo.Setup(x => x.GetPendingByBatchAsync(1)).ReturnsAsync(new List<InitExecutionRecord>
        {
            Init(1, batchId: 1, index: 0, status: StepStatus.Pending, workerId: "w1", functionName: "fn0", runbookVersion: 2)
        });

        await _sut.HandleAsync(message);

        // Should NOT insert any init executions (idempotent skip)
        _initRepo.Verify(x => x.InsertAsync(It.IsAny<InitExecutionRecord>(), It.IsAny<IDbTransaction>()),
            Times.Never);
        // Should still dispatch
        _workerDispatcher.Verify(x => x.DispatchJobAsync(It.IsAny<WorkerJobMessage>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_CreatesForNewVersion_WhenOldVersionExists()
    {
        var message = CreateMessage(batchId: 1, runbookName: "test", version: 2);
        SetupBatchAndRunbook(message, initStepCount: 1);

        // Existing init executions for version 1 only
        _initRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(new List<InitExecutionRecord>
        {
            Init(1, batchId: 1, index: 0, status: StepStatus.Succeeded, workerId: "w1", functionName: "fn0", runbookVersion: 1)
        });

        _initRepo.Setup(x => x.GetPendingByBatchAsync(1)).ReturnsAsync(new List<InitExecutionRecord>
        {
            Init(2, batchId: 1, index: 0, status: StepStatus.Pending, workerId: "w1", functionName: "fn0", runbookVersion: 2)
        });

        await _sut.HandleAsync(message);

        // Should insert new init execution for version 2
        _initRepo.Verify(x => x.InsertAsync(It.IsAny<InitExecutionRecord>(), It.IsAny<IDbTransaction>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_ResolvesRetryConfig_FromGlobalAndStepLevel()
    {
        var message = CreateMessage(batchId: 1, runbookName: "test", version: 1);
        var definition = new RunbookDefinition
        {
            Name = "test",
            DataSource = new DataSourceConfig { PrimaryKey = "id" },
            Retry = new RetryConfig { MaxRetries = 3, Interval = "2m" },
            Init = new List<StepDefinition>
            {
                new()
                {
                    Name = "global-retry-step",
                    WorkerId = "w1",
                    Function = "fn1"
                    // No step-level retry — should inherit global
                },
                new()
                {
                    Name = "step-retry-step",
                    WorkerId = "w1",
                    Function = "fn2",
                    Retry = new RetryConfig { MaxRetries = 5, Interval = "30s" }
                }
            }
        };

        _batchRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new BatchRecord { Id = 1 });
        _runbookRepo.Setup(x => x.GetByNameAndVersionAsync("test", 1))
            .ReturnsAsync(new RunbookRecord { Id = 1, Name = "test", Version = 1, YamlContent = "yaml" });
        _runbookParser.Setup(x => x.Parse("yaml")).Returns(definition);
        _initRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(new List<InitExecutionRecord>());
        _initRepo.Setup(x => x.GetPendingByBatchAsync(1)).ReturnsAsync(new List<InitExecutionRecord>());
        _phaseEvaluator.Setup(x => x.ParseDurationSeconds("2m")).Returns(120);
        _phaseEvaluator.Setup(x => x.ParseDurationSeconds("30s")).Returns(30);

        InitExecutionRecord? firstInserted = null;
        InitExecutionRecord? secondInserted = null;
        var insertCount = 0;
        _initRepo.Setup(x => x.InsertAsync(It.IsAny<InitExecutionRecord>(), It.IsAny<IDbTransaction>()))
            .Callback<InitExecutionRecord, IDbTransaction>((r, _) =>
            {
                if (insertCount == 0) firstInserted = r;
                else secondInserted = r;
                insertCount++;
            })
            .ReturnsAsync(1);

        await _sut.HandleAsync(message);

        // First step should inherit global retry
        firstInserted.Should().NotBeNull();
        firstInserted!.MaxRetries.Should().Be(3);
        firstInserted.RetryIntervalSec.Should().Be(120);

        // Second step should use step-level retry
        secondInserted.Should().NotBeNull();
        secondInserted!.MaxRetries.Should().Be(5);
        secondInserted.RetryIntervalSec.Should().Be(30);
    }

    [Fact]
    public async Task HandleAsync_NoInitSteps_SetsActive()
    {
        var message = CreateMessage(batchId: 1, runbookName: "test", version: 1);

        _batchRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new BatchRecord { Id = 1 });
        _runbookRepo.Setup(x => x.GetByNameAndVersionAsync("test", 1))
            .ReturnsAsync(new RunbookRecord { Id = 1, Name = "test", Version = 1, YamlContent = "yaml" });
        _runbookParser.Setup(x => x.Parse("yaml")).Returns(new RunbookDefinition
        {
            Name = "test",
            DataSource = new DataSourceConfig { PrimaryKey = "id" },
            Init = new List<StepDefinition>() // No init steps
        });
        _initRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(new List<InitExecutionRecord>());
        _initRepo.Setup(x => x.GetPendingByBatchAsync(1)).ReturnsAsync(new List<InitExecutionRecord>());

        await _sut.HandleAsync(message);

        _batchRepo.Verify(x => x.SetActiveAsync(1), Times.Once);
        _workerDispatcher.Verify(x => x.DispatchJobAsync(It.IsAny<WorkerJobMessage>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_BatchNotFound_ReturnsEarly()
    {
        var message = CreateMessage(batchId: 99, runbookName: "test", version: 1);
        _batchRepo.Setup(x => x.GetByIdAsync(99)).ReturnsAsync((BatchRecord?)null);

        await _sut.HandleAsync(message);

        _runbookRepo.Verify(x => x.GetByNameAndVersionAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_RunbookNotFound_SetsFailed()
    {
        var message = CreateMessage(batchId: 1, runbookName: "missing", version: 1);
        _batchRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new BatchRecord { Id = 1 });
        _runbookRepo.Setup(x => x.GetByNameAndVersionAsync("missing", 1)).ReturnsAsync((RunbookRecord?)null);

        await _sut.HandleAsync(message);

        _batchRepo.Verify(x => x.SetFailedAsync(1), Times.Once);
    }

    // ---- Helpers ----

    private static BatchInitMessage CreateMessage(int batchId, string runbookName, int version) =>
        new()
        {
            BatchId = batchId,
            RunbookName = runbookName,
            RunbookVersion = version,
            BatchStartTime = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc),
            MemberCount = 5
        };

    private void SetupBatchAndRunbook(BatchInitMessage message, int initStepCount)
    {
        _batchRepo.Setup(x => x.GetByIdAsync(message.BatchId))
            .ReturnsAsync(new BatchRecord { Id = message.BatchId });

        var initSteps = new List<StepDefinition>();
        for (int i = 0; i < initStepCount; i++)
        {
            initSteps.Add(new StepDefinition
            {
                Name = $"init-step-{i}",
                WorkerId = "w1",
                Function = $"fn{i}"
            });
        }

        var definition = new RunbookDefinition
        {
            Name = message.RunbookName,
            DataSource = new DataSourceConfig { PrimaryKey = "id" },
            Init = initSteps
        };

        _runbookRepo.Setup(x => x.GetByNameAndVersionAsync(message.RunbookName, message.RunbookVersion))
            .ReturnsAsync(new RunbookRecord
            {
                Id = 1,
                Name = message.RunbookName,
                Version = message.RunbookVersion,
                YamlContent = "yaml"
            });
        _runbookParser.Setup(x => x.Parse("yaml")).Returns(definition);
    }

    private static InitExecutionRecord Init(int id, int batchId, int index, string status,
        string? workerId = null, string? functionName = null, int runbookVersion = 1) =>
        new()
        {
            Id = id,
            BatchId = batchId,
            StepIndex = index,
            StepName = $"init-step-{index}",
            Status = status,
            WorkerId = workerId,
            FunctionName = functionName,
            RunbookVersion = runbookVersion
        };
}
