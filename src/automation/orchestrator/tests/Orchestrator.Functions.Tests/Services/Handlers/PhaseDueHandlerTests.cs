using System.Text.Json;
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

public class PhaseDueHandlerTests
{
    private readonly Mock<IBatchRepository> _batchRepo = new();
    private readonly Mock<IRunbookRepository> _runbookRepo = new();
    private readonly Mock<IPhaseExecutionRepository> _phaseRepo = new();
    private readonly Mock<IStepExecutionRepository> _stepRepo = new();
    private readonly Mock<IMemberRepository> _memberRepo = new();
    private readonly Mock<IWorkerDispatcher> _workerDispatcher = new();
    private readonly Mock<IRunbookParser> _runbookParser = new();
    private readonly Mock<ITemplateResolver> _templateResolver = new();
    private readonly Mock<IPhaseEvaluator> _phaseEvaluator = new();
    private readonly Mock<IDynamicTableReader> _dynamicTableReader = new();
    private readonly Mock<IPhaseProgressionService> _progressionService = new();
    private readonly Mock<IDbConnectionFactory> _db = new();
    private readonly PhaseDueHandler _sut;

    public PhaseDueHandlerTests()
    {
        _workerDispatcher.Setup(x => x.DispatchJobsAsync(It.IsAny<IEnumerable<WorkerJobMessage>>()))
            .Returns(Task.CompletedTask);
        _stepRepo.Setup(x => x.SetDispatchedAsync(It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        _sut = new PhaseDueHandler(
            _batchRepo.Object,
            _runbookRepo.Object,
            _phaseRepo.Object,
            _stepRepo.Object,
            _memberRepo.Object,
            _workerDispatcher.Object,
            _runbookParser.Object,
            _templateResolver.Object,
            _phaseEvaluator.Object,
            _dynamicTableReader.Object,
            _progressionService.Object,
            _db.Object,
            Mock.Of<ILogger<PhaseDueHandler>>());
    }

    [Fact]
    public async Task HandleAsync_TerminalPhase_SkipsProcessing()
    {
        _phaseRepo.Setup(x => x.GetByIdAsync(10))
            .ReturnsAsync(new PhaseExecutionRecord { Id = 10, BatchId = 1, Status = PhaseStatus.Completed });

        var message = new PhaseDueMessage
        {
            PhaseExecutionId = 10,
            BatchId = 1,
            PhaseName = "phase1",
            RunbookName = "runbook1",
            RunbookVersion = 1
        };

        await _sut.HandleAsync(message);

        _stepRepo.Verify(x => x.GetByPhaseExecutionAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_InitialDispatch_DispatchesFirstStepForAllMembers()
    {
        SetupPhase(10, batchId: 1);

        // Steps already exist: step[0] pending for members 100 and 200
        var steps = new List<StepExecutionRecord>
        {
            Step(1, phaseId: 10, memberId: 100, index: 0, status: StepStatus.Pending,
                 workerId: "w1", functionName: "fn1"),
            Step(2, phaseId: 10, memberId: 100, index: 1, status: StepStatus.Pending),
            Step(3, phaseId: 10, memberId: 200, index: 0, status: StepStatus.Pending,
                 workerId: "w1", functionName: "fn1"),
            Step(4, phaseId: 10, memberId: 200, index: 1, status: StepStatus.Pending)
        };
        _stepRepo.Setup(x => x.GetByPhaseExecutionAsync(10)).ReturnsAsync(steps);

        var message = CreateMessage(10, 1, "phase1", "runbook1", 1);
        await _sut.HandleAsync(message);

        // Should dispatch step[0] for both members (steps 1 and 3)
        _workerDispatcher.Verify(x => x.DispatchJobsAsync(It.Is<IEnumerable<WorkerJobMessage>>(jobs =>
            jobs.Count() == 2)), Times.Once);
        _stepRepo.Verify(x => x.SetDispatchedAsync(1, "step-1"), Times.Once);
        _stepRepo.Verify(x => x.SetDispatchedAsync(3, "step-3"), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_MixedProgress_DispatchesEachMembersNextStep()
    {
        SetupPhase(10, batchId: 1);

        // Member 100: step[0]=succeeded, step[1]=pending (should dispatch step[1])
        // Member 200: step[0]=dispatched (should skip — in progress)
        var steps = new List<StepExecutionRecord>
        {
            Step(1, phaseId: 10, memberId: 100, index: 0, status: StepStatus.Succeeded),
            Step(2, phaseId: 10, memberId: 100, index: 1, status: StepStatus.Pending,
                 workerId: "w1", functionName: "fn2"),
            Step(3, phaseId: 10, memberId: 200, index: 0, status: StepStatus.Dispatched),
            Step(4, phaseId: 10, memberId: 200, index: 1, status: StepStatus.Pending)
        };
        _stepRepo.Setup(x => x.GetByPhaseExecutionAsync(10)).ReturnsAsync(steps);

        var message = CreateMessage(10, 1, "phase1", "runbook1", 1);
        await _sut.HandleAsync(message);

        // Should dispatch only step 2 (member 100's next)
        _workerDispatcher.Verify(x => x.DispatchJobsAsync(It.Is<IEnumerable<WorkerJobMessage>>(jobs =>
            jobs.Count() == 1 && jobs.First().JobId == "step-2")), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_AllMembersFailed_NoSteps_CallsCheckBatchCompletion()
    {
        SetupPhase(10, batchId: 1);
        _stepRepo.Setup(x => x.GetByPhaseExecutionAsync(10)).ReturnsAsync(new List<StepExecutionRecord>());
        _memberRepo.Setup(x => x.GetActiveByBatchAsync(1)).ReturnsAsync(new List<BatchMemberRecord>());

        var message = CreateMessage(10, 1, "phase1", "runbook1", 1);
        await _sut.HandleAsync(message);

        _phaseRepo.Verify(x => x.SetFailedAsync(10), Times.Once);
        _progressionService.Verify(x => x.CheckBatchCompletionAsync(1), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_EmptyPhase_WithActiveMembers_MarksCompleted()
    {
        SetupPhase(10, batchId: 1);
        _stepRepo.Setup(x => x.GetByPhaseExecutionAsync(10)).ReturnsAsync(new List<StepExecutionRecord>());
        _memberRepo.Setup(x => x.GetActiveByBatchAsync(1)).ReturnsAsync(new List<BatchMemberRecord>
        {
            new() { Id = 100, BatchId = 1, Status = MemberStatus.Active }
        });

        var message = CreateMessage(10, 1, "phase1", "runbook1", 1);
        await _sut.HandleAsync(message);

        _phaseRepo.Verify(x => x.SetCompletedAsync(10), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_NoStepsToDispatch_AllTerminal_CallsCheckPhaseCompletion()
    {
        SetupPhase(10, batchId: 1);

        // All steps already terminal
        var steps = new List<StepExecutionRecord>
        {
            Step(1, phaseId: 10, memberId: 100, index: 0, status: StepStatus.Succeeded),
            Step(2, phaseId: 10, memberId: 100, index: 1, status: StepStatus.Succeeded)
        };
        _stepRepo.Setup(x => x.GetByPhaseExecutionAsync(10)).ReturnsAsync(steps);

        var message = CreateMessage(10, 1, "phase1", "runbook1", 1);
        await _sut.HandleAsync(message);

        _workerDispatcher.Verify(x => x.DispatchJobsAsync(It.IsAny<IEnumerable<WorkerJobMessage>>()), Times.Never);
        _progressionService.Verify(x => x.CheckPhaseCompletionAsync(10), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_SkipsMemberWithFailedStep()
    {
        SetupPhase(10, batchId: 1);

        // Member 100: step[0]=failed → should not dispatch step[1]
        // Member 200: step[0]=pending → should dispatch
        var steps = new List<StepExecutionRecord>
        {
            Step(1, phaseId: 10, memberId: 100, index: 0, status: StepStatus.Failed),
            Step(2, phaseId: 10, memberId: 100, index: 1, status: StepStatus.Cancelled),
            Step(3, phaseId: 10, memberId: 200, index: 0, status: StepStatus.Pending,
                 workerId: "w1", functionName: "fn1"),
            Step(4, phaseId: 10, memberId: 200, index: 1, status: StepStatus.Pending)
        };
        _stepRepo.Setup(x => x.GetByPhaseExecutionAsync(10)).ReturnsAsync(steps);

        var message = CreateMessage(10, 1, "phase1", "runbook1", 1);
        await _sut.HandleAsync(message);

        // Should only dispatch step 3 (member 200's first step)
        _workerDispatcher.Verify(x => x.DispatchJobsAsync(It.Is<IEnumerable<WorkerJobMessage>>(jobs =>
            jobs.Count() == 1 && jobs.First().JobId == "step-3")), Times.Once);
    }

    // ---- Helpers ----

    private void SetupPhase(int id, int batchId)
    {
        _phaseRepo.Setup(x => x.GetByIdAsync(id))
            .ReturnsAsync(new PhaseExecutionRecord { Id = id, BatchId = batchId, Status = PhaseStatus.Pending });
        // Return existing steps to skip step creation
        _stepRepo.Setup(x => x.GetByPhaseExecutionAsync(id))
            .ReturnsAsync(new List<StepExecutionRecord> { new() });
    }

    private static PhaseDueMessage CreateMessage(int phaseId, int batchId, string phaseName, string runbookName, int version) =>
        new()
        {
            PhaseExecutionId = phaseId,
            BatchId = batchId,
            PhaseName = phaseName,
            RunbookName = runbookName,
            RunbookVersion = version
        };

    private static StepExecutionRecord Step(int id, int phaseId, int memberId, int index, string status,
        string? workerId = null, string? functionName = null) =>
        new()
        {
            Id = id,
            PhaseExecutionId = phaseId,
            BatchMemberId = memberId,
            StepIndex = index,
            StepName = $"step-{index}",
            Status = status,
            WorkerId = workerId,
            FunctionName = functionName
        };
}
