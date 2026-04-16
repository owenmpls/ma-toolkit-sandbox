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

public class DispatchTimeoutHandlerTests
{
    private readonly Mock<IStepExecutionRepository> _stepRepo = new();
    private readonly Mock<IInitExecutionRepository> _initRepo = new();
    private readonly Mock<IBatchRepository> _batchRepo = new();
    private readonly Mock<IPhaseProgressionService> _progressionService = new();
    private readonly DispatchTimeoutHandler _sut;

    public DispatchTimeoutHandlerTests()
    {
        _sut = new DispatchTimeoutHandler(
            _stepRepo.Object,
            _initRepo.Object,
            _batchRepo.Object,
            _progressionService.Object,
            Mock.Of<ILogger<DispatchTimeoutHandler>>());
    }

    [Fact]
    public async Task HandleAsync_StepStillDispatched_FailsStepAndHandlesMemberFailure()
    {
        var step = new StepExecutionRecord
        {
            Id = 42,
            PhaseExecutionId = 10,
            BatchMemberId = 100,
            Status = StepStatus.Dispatched,
            DispatchedAt = DateTime.UtcNow.AddMinutes(-35)
        };
        _stepRepo.Setup(x => x.GetByIdAsync(42)).ReturnsAsync(step);
        _stepRepo.Setup(x => x.SetFailedAsync(42, It.IsAny<string>())).ReturnsAsync(true);

        var message = new DispatchTimeoutMessage
        {
            StepExecutionId = 42,
            IsInitStep = false,
            RunbookName = "test-runbook",
            RunbookVersion = 1,
            BatchId = 5,
            StepName = "Set-Something",
            DispatchedAt = step.DispatchedAt!.Value
        };

        await _sut.HandleAsync(message);

        _stepRepo.Verify(x => x.SetFailedAsync(42, It.Is<string>(s => s.Contains("Dispatch timeout"))), Times.Once);
        _progressionService.Verify(x => x.HandleMemberFailureAsync(10, 100), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_StepAlreadySucceeded_SkipsTimeout()
    {
        var step = new StepExecutionRecord
        {
            Id = 42,
            Status = StepStatus.Succeeded
        };
        _stepRepo.Setup(x => x.GetByIdAsync(42)).ReturnsAsync(step);

        var message = new DispatchTimeoutMessage
        {
            StepExecutionId = 42,
            IsInitStep = false,
            RunbookName = "test-runbook",
            RunbookVersion = 1,
            BatchId = 5,
            StepName = "Set-Something",
            DispatchedAt = DateTime.UtcNow.AddMinutes(-35)
        };

        await _sut.HandleAsync(message);

        _stepRepo.Verify(x => x.SetFailedAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        _progressionService.Verify(x => x.HandleMemberFailureAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_StepNotFound_DoesNothing()
    {
        _stepRepo.Setup(x => x.GetByIdAsync(99)).ReturnsAsync((StepExecutionRecord?)null);

        var message = new DispatchTimeoutMessage
        {
            StepExecutionId = 99,
            IsInitStep = false,
            RunbookName = "test-runbook",
            RunbookVersion = 1,
            BatchId = 5,
            StepName = "Set-Something",
            DispatchedAt = DateTime.UtcNow.AddMinutes(-35)
        };

        await _sut.HandleAsync(message);

        _stepRepo.Verify(x => x.SetFailedAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        _progressionService.Verify(x => x.HandleMemberFailureAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_InitStillDispatched_FailsInitAndFailsBatch()
    {
        var initExec = new InitExecutionRecord
        {
            Id = 7,
            BatchId = 10,
            Status = StepStatus.Dispatched,
            DispatchedAt = DateTime.UtcNow.AddMinutes(-35)
        };
        _initRepo.Setup(x => x.GetByIdAsync(7)).ReturnsAsync(initExec);
        _initRepo.Setup(x => x.SetFailedAsync(7, It.IsAny<string>())).ReturnsAsync(true);
        _batchRepo.Setup(x => x.SetFailedAsync(10)).ReturnsAsync(true);

        var message = new DispatchTimeoutMessage
        {
            StepExecutionId = 7,
            IsInitStep = true,
            RunbookName = "test-runbook",
            RunbookVersion = 1,
            BatchId = 10,
            StepName = "New-MigrationBatch",
            DispatchedAt = initExec.DispatchedAt!.Value
        };

        await _sut.HandleAsync(message);

        _initRepo.Verify(x => x.SetFailedAsync(7, It.Is<string>(s => s.Contains("Dispatch timeout"))), Times.Once);
        _batchRepo.Verify(x => x.SetFailedAsync(10), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_InitAlreadySucceeded_SkipsTimeout()
    {
        var initExec = new InitExecutionRecord
        {
            Id = 7,
            BatchId = 10,
            Status = StepStatus.Succeeded
        };
        _initRepo.Setup(x => x.GetByIdAsync(7)).ReturnsAsync(initExec);

        var message = new DispatchTimeoutMessage
        {
            StepExecutionId = 7,
            IsInitStep = true,
            RunbookName = "test-runbook",
            RunbookVersion = 1,
            BatchId = 10,
            StepName = "New-MigrationBatch",
            DispatchedAt = DateTime.UtcNow.AddMinutes(-35)
        };

        await _sut.HandleAsync(message);

        _initRepo.Verify(x => x.SetFailedAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        _batchRepo.Verify(x => x.SetFailedAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_InitNotFound_DoesNothing()
    {
        _initRepo.Setup(x => x.GetByIdAsync(99)).ReturnsAsync((InitExecutionRecord?)null);

        var message = new DispatchTimeoutMessage
        {
            StepExecutionId = 99,
            IsInitStep = true,
            RunbookName = "test-runbook",
            RunbookVersion = 1,
            BatchId = 10,
            StepName = "New-MigrationBatch",
            DispatchedAt = DateTime.UtcNow.AddMinutes(-35)
        };

        await _sut.HandleAsync(message);

        _initRepo.Verify(x => x.SetFailedAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        _batchRepo.Verify(x => x.SetFailedAsync(It.IsAny<int>()), Times.Never);
    }
}
