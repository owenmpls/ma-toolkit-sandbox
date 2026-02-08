using Microsoft.Extensions.Logging;
using Moq;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Services.Repositories;
using Orchestrator.Functions.Services;
using Orchestrator.Functions.Services.Handlers;
using Xunit;

namespace Orchestrator.Functions.Tests.Services.Handlers;

public class RetryCheckHandlerTests
{
    private readonly Mock<IStepExecutionRepository> _stepRepo = new();
    private readonly Mock<IInitExecutionRepository> _initRepo = new();
    private readonly Mock<IWorkerDispatcher> _workerDispatcher = new();
    private readonly RetryCheckHandler _sut;

    public RetryCheckHandlerTests()
    {
        _sut = new RetryCheckHandler(
            _stepRepo.Object,
            _initRepo.Object,
            _workerDispatcher.Object,
            Mock.Of<ILogger<RetryCheckHandler>>());
    }

    [Fact]
    public async Task HandleAsync_StepPending_RedispatchesStep()
    {
        var step = new StepExecutionRecord
        {
            Id = 42,
            Status = StepStatus.Pending,
            RetryCount = 1,
            WorkerId = "cloud-worker",
            FunctionName = "Set-Something",
            ParamsJson = "{\"key\":\"value\"}"
        };
        _stepRepo.Setup(x => x.GetByIdAsync(42)).ReturnsAsync(step);
        _workerDispatcher.Setup(x => x.DispatchJobAsync(It.IsAny<WorkerJobMessage>()))
            .ReturnsAsync("step-42-retry-1");
        _stepRepo.Setup(x => x.SetDispatchedAsync(42, "step-42-retry-1")).ReturnsAsync(true);

        var message = new RetryCheckMessage
        {
            StepExecutionId = 42,
            IsInitStep = false,
            RunbookName = "test-runbook",
            RunbookVersion = 1,
            BatchId = 5
        };

        await _sut.HandleAsync(message);

        _workerDispatcher.Verify(x => x.DispatchJobAsync(
            It.Is<WorkerJobMessage>(j =>
                j.JobId == "step-42-retry-1" &&
                j.WorkerId == "cloud-worker" &&
                j.FunctionName == "Set-Something" &&
                j.CorrelationData!.StepExecutionId == 42 &&
                !j.CorrelationData.IsInitStep)), Times.Once);
        _stepRepo.Verify(x => x.SetDispatchedAsync(42, "step-42-retry-1"), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_StepCancelled_SkipsDispatch()
    {
        var step = new StepExecutionRecord
        {
            Id = 42,
            Status = StepStatus.Cancelled,
            RetryCount = 1,
            WorkerId = "cloud-worker",
            FunctionName = "Set-Something"
        };
        _stepRepo.Setup(x => x.GetByIdAsync(42)).ReturnsAsync(step);

        var message = new RetryCheckMessage
        {
            StepExecutionId = 42,
            IsInitStep = false,
            RunbookName = "test-runbook",
            RunbookVersion = 1,
            BatchId = 5
        };

        await _sut.HandleAsync(message);

        _workerDispatcher.Verify(x => x.DispatchJobAsync(It.IsAny<WorkerJobMessage>()), Times.Never);
        _stepRepo.Verify(x => x.SetDispatchedAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_InitPending_RedispatchesInit()
    {
        var initExec = new InitExecutionRecord
        {
            Id = 7,
            BatchId = 10,
            Status = StepStatus.Pending,
            RetryCount = 2,
            WorkerId = "cloud-worker",
            FunctionName = "New-MigrationBatch",
            ParamsJson = "{\"BatchName\":\"batch-10\"}"
        };
        _initRepo.Setup(x => x.GetByIdAsync(7)).ReturnsAsync(initExec);
        _workerDispatcher.Setup(x => x.DispatchJobAsync(It.IsAny<WorkerJobMessage>()))
            .ReturnsAsync("init-7-retry-2");
        _initRepo.Setup(x => x.SetDispatchedAsync(7, "init-7-retry-2")).ReturnsAsync(true);

        var message = new RetryCheckMessage
        {
            StepExecutionId = 7,
            IsInitStep = true,
            RunbookName = "test-runbook",
            RunbookVersion = 1,
            BatchId = 10
        };

        await _sut.HandleAsync(message);

        _workerDispatcher.Verify(x => x.DispatchJobAsync(
            It.Is<WorkerJobMessage>(j =>
                j.JobId == "init-7-retry-2" &&
                j.WorkerId == "cloud-worker" &&
                j.FunctionName == "New-MigrationBatch" &&
                j.CorrelationData!.InitExecutionId == 7 &&
                j.CorrelationData.IsInitStep)), Times.Once);
        _initRepo.Verify(x => x.SetDispatchedAsync(7, "init-7-retry-2"), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_StepNotFound_DoesNothing()
    {
        _stepRepo.Setup(x => x.GetByIdAsync(99)).ReturnsAsync((StepExecutionRecord?)null);

        var message = new RetryCheckMessage
        {
            StepExecutionId = 99,
            IsInitStep = false,
            RunbookName = "test-runbook",
            RunbookVersion = 1,
            BatchId = 5
        };

        await _sut.HandleAsync(message);

        _workerDispatcher.Verify(x => x.DispatchJobAsync(It.IsAny<WorkerJobMessage>()), Times.Never);
    }
}
