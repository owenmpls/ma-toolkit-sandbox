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
    private readonly Mock<IDynamicTableReader> _dynamicTableReader = new();
    private readonly Mock<IPhaseProgressionService> _progressionService = new();
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
            _dynamicTableReader.Object,
            _progressionService.Object,
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
            new RunbookRecord { Id = 1, Name = "runbook1", Version = 1, YamlContent = "test" });
        _runbookParser.Setup(x => x.Parse(It.IsAny<string>())).Returns(new MaToolkit.Automation.Shared.Models.Yaml.RunbookDefinition());
        _batchRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new BatchRecord { Id = 1 });

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
}
