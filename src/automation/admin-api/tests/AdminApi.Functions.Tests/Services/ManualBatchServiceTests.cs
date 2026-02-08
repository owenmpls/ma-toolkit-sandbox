using System.Data;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using AdminApi.Functions.Services;
using MaToolkit.Automation.Shared.Services.Repositories;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AdminApi.Functions.Tests.Services;

public class ManualBatchServiceTests
{
    private readonly ManualBatchService _sut;
    private readonly Mock<IBatchRepository> _batchRepoMock;
    private readonly Mock<IMemberRepository> _memberRepoMock;
    private readonly Mock<IPhaseExecutionRepository> _phaseRepoMock;
    private readonly Mock<IInitExecutionRepository> _initRepoMock;
    private readonly Mock<IPhaseEvaluator> _phaseEvaluatorMock;
    private readonly Mock<IDbConnectionFactory> _dbMock;
    private readonly Mock<ILogger<ManualBatchService>> _loggerMock;
    private readonly Mock<ServiceBusClient> _serviceBusClientMock;
    private readonly Mock<ServiceBusSender> _senderMock;

    public ManualBatchServiceTests()
    {
        _batchRepoMock = new Mock<IBatchRepository>();
        _memberRepoMock = new Mock<IMemberRepository>();
        _phaseRepoMock = new Mock<IPhaseExecutionRepository>();
        _initRepoMock = new Mock<IInitExecutionRepository>();
        _phaseEvaluatorMock = new Mock<IPhaseEvaluator>();
        _dbMock = new Mock<IDbConnectionFactory>();
        _loggerMock = new Mock<ILogger<ManualBatchService>>();

        // Setup Service Bus mock
        _serviceBusClientMock = new Mock<ServiceBusClient>();
        _senderMock = new Mock<ServiceBusSender>();
        _senderMock.Setup(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), default))
            .Returns(Task.CompletedTask);
        _serviceBusClientMock.Setup(x => x.CreateSender(It.IsAny<string>()))
            .Returns(_senderMock.Object);

        // Setup phase evaluator defaults
        _phaseEvaluatorMock.Setup(x => x.ParseOffsetMinutes(It.IsAny<string>())).Returns(0);
        _phaseEvaluatorMock.Setup(x => x.ParseDurationSeconds(It.IsAny<string>())).Returns(0);

        _sut = new ManualBatchService(
            _batchRepoMock.Object,
            _memberRepoMock.Object,
            _phaseRepoMock.Object,
            _initRepoMock.Object,
            _phaseEvaluatorMock.Object,
            _dbMock.Object,
            _loggerMock.Object,
            _serviceBusClientMock.Object);
    }

    private static RunbookRecord CreateRunbook()
    {
        return new RunbookRecord
        {
            Id = 1,
            Name = "test-runbook",
            Version = 1,
            DataTableName = "runbook_test_runbook_v1"
        };
    }

    private static RunbookDefinition CreateDefinition(bool withInit = false)
    {
        var definition = new RunbookDefinition
        {
            Name = "test-runbook",
            DataSource = new DataSourceConfig
            {
                Type = "dataverse",
                PrimaryKey = "user_id",
                Query = "SELECT user_id FROM users"
            },
            Phases = new List<PhaseDefinition>
            {
                new()
                {
                    Name = "phase1",
                    Offset = "T-1d",
                    Steps = new List<StepDefinition>
                    {
                        new()
                        {
                            Name = "step1",
                            WorkerId = "worker1",
                            Function = "DoWork"
                        }
                    }
                },
                new()
                {
                    Name = "phase2",
                    Offset = "T-0",
                    Steps = new List<StepDefinition>
                    {
                        new()
                        {
                            Name = "step2",
                            WorkerId = "worker1",
                            Function = "DoMoreWork"
                        }
                    }
                }
            }
        };

        if (withInit)
        {
            definition.Init = new List<StepDefinition>
            {
                new()
                {
                    Name = "init-step",
                    WorkerId = "admin-worker",
                    Function = "SetupBatch"
                }
            };
        }

        return definition;
    }

    private static DataTable CreateMemberData(int count)
    {
        var table = new DataTable();
        table.Columns.Add("user_id", typeof(string));
        table.Columns.Add("email", typeof(string));

        for (int i = 1; i <= count; i++)
        {
            var row = table.NewRow();
            row["user_id"] = $"user{i}";
            row["email"] = $"user{i}@example.com";
            table.Rows.Add(row);
        }

        return table;
    }

    #region AdvanceBatch - Basic Tests

    [Fact]
    public async Task AdvanceBatchAsync_NonManualBatch_ReturnsError()
    {
        var batch = new BatchRecord { Id = 1, IsManual = false };
        var runbook = CreateRunbook();
        var definition = CreateDefinition();

        var result = await _sut.AdvanceBatchAsync(batch, runbook, definition);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not a manual batch");
    }

    #endregion

    #region AdvanceBatch - Init Step Tests

    [Fact]
    public async Task AdvanceBatchAsync_WithPendingInit_DispatchesInit()
    {
        var batch = new BatchRecord
        {
            Id = 1,
            IsManual = true,
            Status = BatchStatus.Detected
        };
        var runbook = CreateRunbook();
        var definition = CreateDefinition(withInit: true);

        // No GetByBatchAsync needed — hasInitSteps comes from definition.Init.Count
        var result = await _sut.AdvanceBatchAsync(batch, runbook, definition);

        result.Success.Should().BeTrue();
        result.Action.Should().Be("init_dispatched");
        result.StepCount.Should().Be(1); // definition.Init.Count
    }

    [Fact]
    public async Task AdvanceBatchAsync_InitInProgress_ReturnsError()
    {
        var batch = new BatchRecord
        {
            Id = 1,
            IsManual = true,
            Status = BatchStatus.InitDispatched
        };
        var runbook = CreateRunbook();
        var definition = CreateDefinition(withInit: true);

        _initRepoMock.Setup(x => x.GetByBatchAsync(1))
            .ReturnsAsync(new List<InitExecutionRecord>
            {
                new() { Id = 1, BatchId = 1, Status = StepStatus.Dispatched }
            });

        var result = await _sut.AdvanceBatchAsync(batch, runbook, definition);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Init steps not yet completed");
    }

    [Fact]
    public async Task AdvanceBatchAsync_InitDispatched_NoExecutionsYet_ReturnsError()
    {
        // Race condition: orchestrator hasn't processed batch-init yet
        var batch = new BatchRecord
        {
            Id = 1,
            IsManual = true,
            Status = BatchStatus.InitDispatched
        };
        var runbook = CreateRunbook();
        var definition = CreateDefinition(withInit: true);

        _initRepoMock.Setup(x => x.GetByBatchAsync(1))
            .ReturnsAsync(new List<InitExecutionRecord>());

        var result = await _sut.AdvanceBatchAsync(batch, runbook, definition);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Init steps not yet completed");
    }

    [Fact]
    public async Task AdvanceBatchAsync_InitCompleted_MovesToActive()
    {
        var batch = new BatchRecord
        {
            Id = 1,
            IsManual = true,
            Status = BatchStatus.InitDispatched
        };
        var runbook = CreateRunbook();
        var definition = CreateDefinition(withInit: true);

        _initRepoMock.Setup(x => x.GetByBatchAsync(1))
            .ReturnsAsync(new List<InitExecutionRecord>
            {
                new() { Id = 1, BatchId = 1, Status = StepStatus.Succeeded }
            });

        _phaseRepoMock.Setup(x => x.GetByBatchAsync(1))
            .ReturnsAsync(new List<PhaseExecutionRecord>
            {
                new() { Id = 1, BatchId = 1, PhaseName = "phase1", OffsetMinutes = 1440, Status = PhaseStatus.Pending }
            });

        _memberRepoMock.Setup(x => x.GetActiveByBatchAsync(1))
            .ReturnsAsync(new List<BatchMemberRecord>
            {
                new() { Id = 1, BatchId = 1, MemberKey = "user1" }
            });

        var result = await _sut.AdvanceBatchAsync(batch, runbook, definition);

        result.Success.Should().BeTrue();
        _batchRepoMock.Verify(x => x.UpdateStatusAsync(1, BatchStatus.Active), Times.Once);
    }

    #endregion

    #region AdvanceBatch - Phase Dispatch Tests

    [Fact]
    public async Task AdvanceBatchAsync_FirstPhase_DispatchesPhase()
    {
        var batch = new BatchRecord
        {
            Id = 1,
            IsManual = true,
            Status = BatchStatus.Active
        };
        var runbook = CreateRunbook();
        var definition = CreateDefinition();

        _phaseRepoMock.Setup(x => x.GetByBatchAsync(1))
            .ReturnsAsync(new List<PhaseExecutionRecord>
            {
                new() { Id = 1, BatchId = 1, PhaseName = "phase1", OffsetMinutes = 1440, Status = PhaseStatus.Pending },
                new() { Id = 2, BatchId = 1, PhaseName = "phase2", OffsetMinutes = 0, Status = PhaseStatus.Pending }
            });

        _memberRepoMock.Setup(x => x.GetActiveByBatchAsync(1))
            .ReturnsAsync(new List<BatchMemberRecord>
            {
                new() { Id = 1, MemberKey = "user1" },
                new() { Id = 2, MemberKey = "user2" }
            });

        var result = await _sut.AdvanceBatchAsync(batch, runbook, definition);

        result.Success.Should().BeTrue();
        result.Action.Should().Be("phase_dispatched");
        result.PhaseName.Should().Be("phase2"); // Lower offset (T-0) comes first
        result.MemberCount.Should().Be(2);
        result.NextPhase.Should().Be("phase1");
    }

    [Fact]
    public async Task AdvanceBatchAsync_PreviousPhaseInProgress_ReturnsError()
    {
        var batch = new BatchRecord
        {
            Id = 1,
            IsManual = true,
            Status = BatchStatus.Active
        };
        var runbook = CreateRunbook();
        var definition = CreateDefinition();

        _phaseRepoMock.Setup(x => x.GetByBatchAsync(1))
            .ReturnsAsync(new List<PhaseExecutionRecord>
            {
                new() { Id = 1, BatchId = 1, PhaseName = "phase1", OffsetMinutes = 0, Status = PhaseStatus.Dispatched },
                new() { Id = 2, BatchId = 1, PhaseName = "phase2", OffsetMinutes = 1440, Status = PhaseStatus.Pending }
            });

        var result = await _sut.AdvanceBatchAsync(batch, runbook, definition);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("phase1");
        result.ErrorMessage.Should().Contain("still in progress");
    }

    [Fact]
    public async Task AdvanceBatchAsync_AllPhasesComplete_MarksCompleted()
    {
        var batch = new BatchRecord
        {
            Id = 1,
            IsManual = true,
            Status = BatchStatus.Active
        };
        var runbook = CreateRunbook();
        var definition = CreateDefinition();

        _phaseRepoMock.Setup(x => x.GetByBatchAsync(1))
            .ReturnsAsync(new List<PhaseExecutionRecord>
            {
                new() { Id = 1, BatchId = 1, PhaseName = "phase1", OffsetMinutes = 1440, Status = PhaseStatus.Completed },
                new() { Id = 2, BatchId = 1, PhaseName = "phase2", OffsetMinutes = 0, Status = PhaseStatus.Completed }
            });

        var result = await _sut.AdvanceBatchAsync(batch, runbook, definition);

        result.Success.Should().BeTrue();
        result.Action.Should().Be("completed");
        _batchRepoMock.Verify(x => x.UpdateStatusAsync(1, BatchStatus.Completed), Times.Once);
    }

    [Fact]
    public async Task AdvanceBatchAsync_LastPendingPhase_NoNextPhase()
    {
        var batch = new BatchRecord
        {
            Id = 1,
            IsManual = true,
            Status = BatchStatus.Active
        };
        var runbook = CreateRunbook();
        var definition = CreateDefinition();

        _phaseRepoMock.Setup(x => x.GetByBatchAsync(1))
            .ReturnsAsync(new List<PhaseExecutionRecord>
            {
                new() { Id = 1, BatchId = 1, PhaseName = "phase1", OffsetMinutes = 0, Status = PhaseStatus.Completed },
                new() { Id = 2, BatchId = 1, PhaseName = "phase2", OffsetMinutes = 1440, Status = PhaseStatus.Pending }
            });

        _memberRepoMock.Setup(x => x.GetActiveByBatchAsync(1))
            .ReturnsAsync(new List<BatchMemberRecord>());

        var result = await _sut.AdvanceBatchAsync(batch, runbook, definition);

        result.Success.Should().BeTrue();
        result.Action.Should().Be("phase_dispatched");
        result.PhaseName.Should().Be("phase2");
        result.NextPhase.Should().BeNull();
    }

    #endregion

    #region AdvanceBatch - Update Calls Tests

    [Fact]
    public async Task AdvanceBatchAsync_DispatchesPhase_UpdatesPhaseStatus()
    {
        var batch = new BatchRecord
        {
            Id = 1,
            IsManual = true,
            Status = BatchStatus.Active
        };
        var runbook = CreateRunbook();
        var definition = CreateDefinition();

        _phaseRepoMock.Setup(x => x.GetByBatchAsync(1))
            .ReturnsAsync(new List<PhaseExecutionRecord>
            {
                new() { Id = 5, BatchId = 1, PhaseName = "phase1", OffsetMinutes = 0, Status = PhaseStatus.Pending }
            });

        _memberRepoMock.Setup(x => x.GetActiveByBatchAsync(1))
            .ReturnsAsync(new List<BatchMemberRecord>());

        await _sut.AdvanceBatchAsync(batch, runbook, definition);

        _phaseRepoMock.Verify(x => x.SetDispatchedAsync(5), Times.Once);
        _batchRepoMock.Verify(x => x.UpdateCurrentPhaseAsync(1, "phase1"), Times.Once);
    }

    #endregion

    #region AdvanceBatch - Service Bus Tests

    [Fact]
    public async Task AdvanceBatchAsync_DispatchesPhase_SendsServiceBusMessage()
    {
        var batch = new BatchRecord
        {
            Id = 1,
            IsManual = true,
            Status = BatchStatus.Active
        };
        var runbook = CreateRunbook();
        var definition = CreateDefinition();

        _phaseRepoMock.Setup(x => x.GetByBatchAsync(1))
            .ReturnsAsync(new List<PhaseExecutionRecord>
            {
                new() { Id = 5, BatchId = 1, PhaseName = "phase1", OffsetMinutes = 0, Status = PhaseStatus.Pending, RunbookVersion = 1 }
            });

        _memberRepoMock.Setup(x => x.GetActiveByBatchAsync(1))
            .ReturnsAsync(new List<BatchMemberRecord>
            {
                new() { Id = 10, BatchId = 1, MemberKey = "user1" }
            });

        await _sut.AdvanceBatchAsync(batch, runbook, definition);

        _serviceBusClientMock.Verify(x => x.CreateSender("orchestrator-events"), Times.Once);
        _senderMock.Verify(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), default), Times.Once);
    }

    [Fact]
    public async Task AdvanceBatchAsync_DispatchesInit_SendsServiceBusMessage()
    {
        var batch = new BatchRecord
        {
            Id = 1,
            IsManual = true,
            Status = BatchStatus.Detected
        };
        var runbook = CreateRunbook();
        var definition = CreateDefinition(withInit: true);

        // No init repo setup needed — Detected path uses definition.Init.Count

        await _sut.AdvanceBatchAsync(batch, runbook, definition);

        _serviceBusClientMock.Verify(x => x.CreateSender("orchestrator-events"), Times.Once);
        _senderMock.Verify(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), default), Times.Once);
    }

    #endregion
}
