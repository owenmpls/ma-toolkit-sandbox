using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;
using Orchestrator.Functions.Services;
using Orchestrator.Functions.Services.Handlers;
using Xunit;

namespace Orchestrator.Functions.Tests.Services.Handlers;

public class ResultProcessorTests
{
    private readonly Mock<IStepExecutionRepository> _stepRepo = new();
    private readonly Mock<IInitExecutionRepository> _initRepo = new();
    private readonly Mock<IBatchRepository> _batchRepo = new();
    private readonly Mock<IPhaseExecutionRepository> _phaseRepo = new();
    private readonly Mock<IRunbookRepository> _runbookRepo = new();
    private readonly Mock<IRunbookParser> _runbookParser = new();
    private readonly Mock<IWorkerDispatcher> _workerDispatcher = new();
    private readonly Mock<IRollbackExecutor> _rollbackExecutor = new();
    private readonly Mock<IMemberRepository> _memberRepo = new();
    private readonly Mock<IPhaseProgressionService> _progressionService = new();
    private readonly Mock<IRetryScheduler> _retryScheduler = new();
    private readonly ResultProcessor _sut;

    public ResultProcessorTests()
    {
        _sut = new ResultProcessor(
            _stepRepo.Object,
            _initRepo.Object,
            _batchRepo.Object,
            _phaseRepo.Object,
            _runbookRepo.Object,
            _runbookParser.Object,
            _workerDispatcher.Object,
            _rollbackExecutor.Object,
            _memberRepo.Object,
            _progressionService.Object,
            _retryScheduler.Object,
            Mock.Of<ILogger<ResultProcessor>>());
    }

    [Fact]
    public async Task ProcessStepResult_Success_CallsCheckMemberProgression()
    {
        var stepExec = new StepExecutionRecord
        {
            Id = 1,
            PhaseExecutionId = 10,
            BatchMemberId = 100,
            Status = StepStatus.Dispatched
        };
        _stepRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(stepExec);
        _stepRepo.Setup(x => x.SetSucceededAsync(1, It.IsAny<string?>())).ReturnsAsync(true);

        var result = new WorkerResultMessage
        {
            JobId = "step-1",
            Status = WorkerResultStatus.Success,
            CorrelationData = new JobCorrelationData
            {
                StepExecutionId = 1,
                RunbookName = "runbook1",
                RunbookVersion = 1
            }
        };

        await _sut.ProcessAsync(result);

        _progressionService.Verify(x => x.CheckMemberProgressionAsync(10, 100, "runbook1", 1), Times.Once);
    }

    [Fact]
    public async Task ProcessStepResult_Failure_CallsHandleMemberFailure()
    {
        var stepExec = new StepExecutionRecord
        {
            Id = 1,
            PhaseExecutionId = 10,
            BatchMemberId = 100,
            Status = StepStatus.Dispatched
        };
        _stepRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(stepExec);
        _stepRepo.Setup(x => x.SetFailedAsync(1, It.IsAny<string>())).ReturnsAsync(true);

        var result = new WorkerResultMessage
        {
            JobId = "step-1",
            Status = WorkerResultStatus.Failure,
            Error = new WorkerErrorInfo { Message = "Something broke" },
            CorrelationData = new JobCorrelationData
            {
                StepExecutionId = 1,
                RunbookName = "runbook1",
                RunbookVersion = 1
            }
        };

        await _sut.ProcessAsync(result);

        _progressionService.Verify(x => x.HandleMemberFailureAsync(10, 100), Times.Once);
    }

    [Fact]
    public async Task ProcessStepResult_Failure_WithRollback_TriggersRollbackAndMemberFailure()
    {
        var stepExec = new StepExecutionRecord
        {
            Id = 1,
            PhaseExecutionId = 10,
            BatchMemberId = 100,
            Status = StepStatus.Dispatched,
            OnFailure = "rollback-step"
        };
        _stepRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(stepExec);
        _stepRepo.Setup(x => x.SetFailedAsync(1, It.IsAny<string>())).ReturnsAsync(true);
        _phaseRepo.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(new PhaseExecutionRecord { Id = 10, BatchId = 1 });
        _runbookRepo.Setup(x => x.GetByNameAndVersionAsync("runbook1", 1)).ReturnsAsync(
            new RunbookRecord { Id = 1, Name = "runbook1", Version = 1, YamlContent = "test", DataTableName = "runbook_runbook1_v1" });
        _runbookParser.Setup(x => x.Parse(It.IsAny<string>())).Returns(new MaToolkit.Automation.Shared.Models.Yaml.RunbookDefinition());
        _batchRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new BatchRecord { Id = 1 });
        _memberRepo.Setup(x => x.GetByIdAsync(100)).ReturnsAsync(new BatchMemberRecord
        {
            Id = 100, MemberKey = "test-key",
            DataJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["MemberKey"] = "test-key" })
        });

        var result = new WorkerResultMessage
        {
            JobId = "step-1",
            Status = WorkerResultStatus.Failure,
            Error = new WorkerErrorInfo { Message = "Error" },
            CorrelationData = new JobCorrelationData
            {
                StepExecutionId = 1,
                RunbookName = "runbook1",
                RunbookVersion = 1
            }
        };

        await _sut.ProcessAsync(result);

        _rollbackExecutor.Verify(x => x.ExecuteRollbackAsync(
            "rollback-step", It.IsAny<MaToolkit.Automation.Shared.Models.Yaml.RunbookDefinition>(),
            It.IsAny<BatchRecord>(), It.IsAny<Dictionary<string, string>>(), 100), Times.Once);
        _progressionService.Verify(x => x.HandleMemberFailureAsync(10, 100), Times.Once);
    }

    [Fact]
    public async Task ProcessStepResult_Failure_WithRollback_MemberNotFound_PassesNullData()
    {
        var stepExec = new StepExecutionRecord
        {
            Id = 1,
            PhaseExecutionId = 10,
            BatchMemberId = 100,
            Status = StepStatus.Dispatched,
            OnFailure = "rollback-step"
        };
        _stepRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(stepExec);
        _stepRepo.Setup(x => x.SetFailedAsync(1, It.IsAny<string>())).ReturnsAsync(true);
        _phaseRepo.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(new PhaseExecutionRecord { Id = 10, BatchId = 1 });
        _runbookRepo.Setup(x => x.GetByNameAndVersionAsync("runbook1", 1)).ReturnsAsync(
            new RunbookRecord { Id = 1, Name = "runbook1", Version = 1, YamlContent = "test", DataTableName = "runbook_runbook1_v1" });
        _runbookParser.Setup(x => x.Parse(It.IsAny<string>())).Returns(new MaToolkit.Automation.Shared.Models.Yaml.RunbookDefinition());
        _batchRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new BatchRecord { Id = 1 });
        _memberRepo.Setup(x => x.GetByIdAsync(100)).ReturnsAsync((BatchMemberRecord?)null);

        var result = new WorkerResultMessage
        {
            JobId = "step-1",
            Status = WorkerResultStatus.Failure,
            Error = new WorkerErrorInfo { Message = "Error" },
            CorrelationData = new JobCorrelationData
            {
                StepExecutionId = 1,
                RunbookName = "runbook1",
                RunbookVersion = 1
            }
        };

        await _sut.ProcessAsync(result);

        _rollbackExecutor.Verify(x => x.ExecuteRollbackAsync(
            "rollback-step", It.IsAny<MaToolkit.Automation.Shared.Models.Yaml.RunbookDefinition>(),
            It.IsAny<BatchRecord>(), null, 100), Times.Once);
        _progressionService.Verify(x => x.HandleMemberFailureAsync(10, 100), Times.Once);
    }

    [Fact]
    public async Task ProcessStepResult_AlreadyTerminal_IgnoresResult()
    {
        // Step already cancelled (e.g., member was failed while this result was in-flight)
        var stepExec = new StepExecutionRecord
        {
            Id = 1,
            PhaseExecutionId = 10,
            BatchMemberId = 100,
            Status = StepStatus.Cancelled
        };
        _stepRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(stepExec);

        var result = new WorkerResultMessage
        {
            JobId = "step-1",
            Status = WorkerResultStatus.Success,
            CorrelationData = new JobCorrelationData
            {
                StepExecutionId = 1,
                RunbookName = "runbook1",
                RunbookVersion = 1
            }
        };

        await _sut.ProcessAsync(result);

        _stepRepo.Verify(x => x.SetSucceededAsync(It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
        _progressionService.Verify(x => x.CheckMemberProgressionAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ProcessStepResult_AlreadySucceeded_IgnoresResult()
    {
        var stepExec = new StepExecutionRecord
        {
            Id = 1,
            PhaseExecutionId = 10,
            BatchMemberId = 100,
            Status = StepStatus.Succeeded
        };
        _stepRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(stepExec);

        var result = new WorkerResultMessage
        {
            JobId = "step-1",
            Status = WorkerResultStatus.Failure,
            Error = new WorkerErrorInfo { Message = "late failure" },
            CorrelationData = new JobCorrelationData
            {
                StepExecutionId = 1,
                RunbookName = "runbook1",
                RunbookVersion = 1
            }
        };

        await _sut.ProcessAsync(result);

        _stepRepo.Verify(x => x.SetFailedAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        _progressionService.Verify(x => x.HandleMemberFailureAsync(
            It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ProcessStepResult_Failure_WithRetries_SchedulesRetry()
    {
        var stepExec = new StepExecutionRecord
        {
            Id = 1,
            PhaseExecutionId = 10,
            BatchMemberId = 100,
            Status = StepStatus.Dispatched,
            MaxRetries = 2,
            RetryCount = 0,
            RetryIntervalSec = 60
        };
        _stepRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(stepExec);
        _stepRepo.Setup(x => x.SetFailedAsync(1, It.IsAny<string>())).ReturnsAsync(true);
        _stepRepo.Setup(x => x.SetRetryPendingAsync(1, It.IsAny<DateTime>())).ReturnsAsync(true);
        _phaseRepo.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(new PhaseExecutionRecord { Id = 10, BatchId = 5 });

        var result = new WorkerResultMessage
        {
            JobId = "step-1",
            Status = WorkerResultStatus.Failure,
            Error = new WorkerErrorInfo { Message = "Transient error" },
            CorrelationData = new JobCorrelationData
            {
                StepExecutionId = 1,
                RunbookName = "runbook1",
                RunbookVersion = 1
            }
        };

        await _sut.ProcessAsync(result);

        _stepRepo.Verify(x => x.SetRetryPendingAsync(1, It.IsAny<DateTime>()), Times.Once);
        _retryScheduler.Verify(x => x.ScheduleRetryAsync(
            It.Is<RetryCheckMessage>(m => m.StepExecutionId == 1 && !m.IsInitStep && m.BatchId == 5),
            It.Is<TimeSpan>(t => t == TimeSpan.FromSeconds(60))), Times.Once);
        _progressionService.Verify(x => x.HandleMemberFailureAsync(
            It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ProcessStepResult_Failure_RetriesExhausted_TriggersFailure()
    {
        var stepExec = new StepExecutionRecord
        {
            Id = 1,
            PhaseExecutionId = 10,
            BatchMemberId = 100,
            Status = StepStatus.Dispatched,
            MaxRetries = 2,
            RetryCount = 2,
            RetryIntervalSec = 60
        };
        _stepRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(stepExec);
        _stepRepo.Setup(x => x.SetFailedAsync(1, It.IsAny<string>())).ReturnsAsync(true);

        var result = new WorkerResultMessage
        {
            JobId = "step-1",
            Status = WorkerResultStatus.Failure,
            Error = new WorkerErrorInfo { Message = "Permanent error" },
            CorrelationData = new JobCorrelationData
            {
                StepExecutionId = 1,
                RunbookName = "runbook1",
                RunbookVersion = 1
            }
        };

        await _sut.ProcessAsync(result);

        _stepRepo.Verify(x => x.SetRetryPendingAsync(It.IsAny<int>(), It.IsAny<DateTime>()), Times.Never);
        _retryScheduler.Verify(x => x.ScheduleRetryAsync(
            It.IsAny<RetryCheckMessage>(), It.IsAny<TimeSpan>()), Times.Never);
        _progressionService.Verify(x => x.HandleMemberFailureAsync(10, 100), Times.Once);
    }

    [Fact]
    public async Task ProcessStepResult_Failure_NoRetryConfig_TriggersFailure()
    {
        var stepExec = new StepExecutionRecord
        {
            Id = 1,
            PhaseExecutionId = 10,
            BatchMemberId = 100,
            Status = StepStatus.Dispatched,
            MaxRetries = null
        };
        _stepRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(stepExec);
        _stepRepo.Setup(x => x.SetFailedAsync(1, It.IsAny<string>())).ReturnsAsync(true);

        var result = new WorkerResultMessage
        {
            JobId = "step-1",
            Status = WorkerResultStatus.Failure,
            Error = new WorkerErrorInfo { Message = "Error" },
            CorrelationData = new JobCorrelationData
            {
                StepExecutionId = 1,
                RunbookName = "runbook1",
                RunbookVersion = 1
            }
        };

        await _sut.ProcessAsync(result);

        _stepRepo.Verify(x => x.SetRetryPendingAsync(It.IsAny<int>(), It.IsAny<DateTime>()), Times.Never);
        _progressionService.Verify(x => x.HandleMemberFailureAsync(10, 100), Times.Once);
    }

    [Fact]
    public async Task ProcessInitResult_Failure_WithRetries_SchedulesRetry()
    {
        var initExec = new InitExecutionRecord
        {
            Id = 5,
            BatchId = 10,
            Status = StepStatus.Dispatched,
            MaxRetries = 3,
            RetryCount = 1,
            RetryIntervalSec = 30
        };
        _initRepo.Setup(x => x.GetByIdAsync(5)).ReturnsAsync(initExec);
        _initRepo.Setup(x => x.SetFailedAsync(5, It.IsAny<string>())).ReturnsAsync(true);
        _initRepo.Setup(x => x.SetRetryPendingAsync(5, It.IsAny<DateTime>())).ReturnsAsync(true);

        var result = new WorkerResultMessage
        {
            JobId = "init-5",
            Status = WorkerResultStatus.Failure,
            Error = new WorkerErrorInfo { Message = "Init transient error" },
            CorrelationData = new JobCorrelationData
            {
                InitExecutionId = 5,
                IsInitStep = true,
                RunbookName = "runbook1",
                RunbookVersion = 1
            }
        };

        await _sut.ProcessAsync(result);

        _initRepo.Verify(x => x.SetRetryPendingAsync(5, It.IsAny<DateTime>()), Times.Once);
        _retryScheduler.Verify(x => x.ScheduleRetryAsync(
            It.Is<RetryCheckMessage>(m => m.StepExecutionId == 5 && m.IsInitStep && m.BatchId == 10),
            It.Is<TimeSpan>(t => t == TimeSpan.FromSeconds(30))), Times.Once);
        _batchRepo.Verify(x => x.SetFailedAsync(It.IsAny<int>()), Times.Never);
    }

    // ---- Output Params Extraction ----

    [Fact]
    public async Task ProcessStepResult_Success_ExtractsOutputParams()
    {
        var stepExec = new StepExecutionRecord
        {
            Id = 1,
            PhaseExecutionId = 10,
            BatchMemberId = 100,
            StepName = "lookup-mailbox",
            Status = StepStatus.Dispatched
        };
        _stepRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(stepExec);
        _stepRepo.Setup(x => x.SetSucceededAsync(1, It.IsAny<string?>())).ReturnsAsync(true);

        // Setup runbook with output_params
        _runbookRepo.Setup(x => x.GetByNameAndVersionAsync("runbook1", 1)).ReturnsAsync(
            new RunbookRecord { Id = 1, Name = "runbook1", Version = 1, YamlContent = "test", DataTableName = "t" });
        _phaseRepo.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(
            new PhaseExecutionRecord { Id = 10, BatchId = 1, PhaseName = "phase1" });
        var stepDef = new MaToolkit.Automation.Shared.Models.Yaml.StepDefinition
        {
            Name = "lookup-mailbox",
            OutputParams = new Dictionary<string, string> { ["MailboxGuid"] = "mailboxGuid" }
        };
        var definition = new MaToolkit.Automation.Shared.Models.Yaml.RunbookDefinition
        {
            Phases = new List<MaToolkit.Automation.Shared.Models.Yaml.PhaseDefinition>
            {
                new() { Name = "phase1", Steps = new List<MaToolkit.Automation.Shared.Models.Yaml.StepDefinition> { stepDef } }
            }
        };
        _runbookParser.Setup(x => x.Parse(It.IsAny<string>())).Returns(definition);

        var result = new WorkerResultMessage
        {
            JobId = "step-1",
            Status = WorkerResultStatus.Success,
            Result = JsonSerializer.SerializeToElement(new { mailboxGuid = "abc-123" }),
            CorrelationData = new JobCorrelationData
            {
                StepExecutionId = 1,
                RunbookName = "runbook1",
                RunbookVersion = 1
            }
        };

        await _sut.ProcessAsync(result);

        _memberRepo.Verify(x => x.MergeWorkerDataAsync(100,
            It.Is<Dictionary<string, string>>(d => d["MailboxGuid"] == "abc-123")), Times.Once);
    }

    [Fact]
    public async Task ProcessStepResult_Success_NoOutputParams_SkipsMerge()
    {
        var stepExec = new StepExecutionRecord
        {
            Id = 1,
            PhaseExecutionId = 10,
            BatchMemberId = 100,
            StepName = "simple-step",
            Status = StepStatus.Dispatched
        };
        _stepRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(stepExec);
        _stepRepo.Setup(x => x.SetSucceededAsync(1, It.IsAny<string?>())).ReturnsAsync(true);

        _runbookRepo.Setup(x => x.GetByNameAndVersionAsync("runbook1", 1)).ReturnsAsync(
            new RunbookRecord { Id = 1, Name = "runbook1", Version = 1, YamlContent = "test", DataTableName = "t" });
        _phaseRepo.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(
            new PhaseExecutionRecord { Id = 10, BatchId = 1, PhaseName = "phase1" });
        var stepDef = new MaToolkit.Automation.Shared.Models.Yaml.StepDefinition
        {
            Name = "simple-step",
            OutputParams = new Dictionary<string, string>()  // empty
        };
        var definition = new MaToolkit.Automation.Shared.Models.Yaml.RunbookDefinition
        {
            Phases = new List<MaToolkit.Automation.Shared.Models.Yaml.PhaseDefinition>
            {
                new() { Name = "phase1", Steps = new List<MaToolkit.Automation.Shared.Models.Yaml.StepDefinition> { stepDef } }
            }
        };
        _runbookParser.Setup(x => x.Parse(It.IsAny<string>())).Returns(definition);

        var result = new WorkerResultMessage
        {
            JobId = "step-1",
            Status = WorkerResultStatus.Success,
            Result = JsonSerializer.SerializeToElement(new { data = "value" }),
            CorrelationData = new JobCorrelationData
            {
                StepExecutionId = 1,
                RunbookName = "runbook1",
                RunbookVersion = 1
            }
        };

        await _sut.ProcessAsync(result);

        _memberRepo.Verify(x => x.MergeWorkerDataAsync(It.IsAny<int>(),
            It.IsAny<Dictionary<string, string>>()), Times.Never);
    }

    [Fact]
    public async Task ProcessStepResult_Success_MissingResultField_WarnsButDoesNotFail()
    {
        var stepExec = new StepExecutionRecord
        {
            Id = 1,
            PhaseExecutionId = 10,
            BatchMemberId = 100,
            StepName = "lookup-step",
            Status = StepStatus.Dispatched
        };
        _stepRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(stepExec);
        _stepRepo.Setup(x => x.SetSucceededAsync(1, It.IsAny<string?>())).ReturnsAsync(true);

        _runbookRepo.Setup(x => x.GetByNameAndVersionAsync("runbook1", 1)).ReturnsAsync(
            new RunbookRecord { Id = 1, Name = "runbook1", Version = 1, YamlContent = "test", DataTableName = "t" });
        _phaseRepo.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(
            new PhaseExecutionRecord { Id = 10, BatchId = 1, PhaseName = "phase1" });
        var stepDef = new MaToolkit.Automation.Shared.Models.Yaml.StepDefinition
        {
            Name = "lookup-step",
            OutputParams = new Dictionary<string, string> { ["MailboxGuid"] = "nonExistentField" }
        };
        var definition = new MaToolkit.Automation.Shared.Models.Yaml.RunbookDefinition
        {
            Phases = new List<MaToolkit.Automation.Shared.Models.Yaml.PhaseDefinition>
            {
                new() { Name = "phase1", Steps = new List<MaToolkit.Automation.Shared.Models.Yaml.StepDefinition> { stepDef } }
            }
        };
        _runbookParser.Setup(x => x.Parse(It.IsAny<string>())).Returns(definition);

        var result = new WorkerResultMessage
        {
            JobId = "step-1",
            Status = WorkerResultStatus.Success,
            Result = JsonSerializer.SerializeToElement(new { otherField = "value" }),
            CorrelationData = new JobCorrelationData
            {
                StepExecutionId = 1,
                RunbookName = "runbook1",
                RunbookVersion = 1
            }
        };

        // Should not throw
        await _sut.ProcessAsync(result);

        // Should not merge (no fields extracted)
        _memberRepo.Verify(x => x.MergeWorkerDataAsync(It.IsAny<int>(),
            It.IsAny<Dictionary<string, string>>()), Times.Never);
        // Progression should still proceed
        _progressionService.Verify(x => x.CheckMemberProgressionAsync(10, 100, "runbook1", 1), Times.Once);
    }

    [Fact]
    public async Task ProcessInitResult_Failure_RetriesExhausted_MarksBatchFailed()
    {
        var initExec = new InitExecutionRecord
        {
            Id = 5,
            BatchId = 10,
            Status = StepStatus.Dispatched,
            MaxRetries = 2,
            RetryCount = 2,
            RetryIntervalSec = 60
        };
        _initRepo.Setup(x => x.GetByIdAsync(5)).ReturnsAsync(initExec);
        _initRepo.Setup(x => x.SetFailedAsync(5, It.IsAny<string>())).ReturnsAsync(true);

        var result = new WorkerResultMessage
        {
            JobId = "init-5",
            Status = WorkerResultStatus.Failure,
            Error = new WorkerErrorInfo { Message = "Permanent init error" },
            CorrelationData = new JobCorrelationData
            {
                InitExecutionId = 5,
                IsInitStep = true,
                RunbookName = "runbook1",
                RunbookVersion = 1
            }
        };

        await _sut.ProcessAsync(result);

        _initRepo.Verify(x => x.SetRetryPendingAsync(It.IsAny<int>(), It.IsAny<DateTime>()), Times.Never);
        _batchRepo.Verify(x => x.SetFailedAsync(10), Times.Once);
    }
}
