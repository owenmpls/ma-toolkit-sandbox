using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Services.Repositories;
using Orchestrator.Functions.Services;
using Xunit;

namespace Orchestrator.Functions.Tests.Services;

public class PhaseProgressionServiceTests
{
    private readonly Mock<IStepExecutionRepository> _stepRepo = new();
    private readonly Mock<IPhaseExecutionRepository> _phaseRepo = new();
    private readonly Mock<IBatchRepository> _batchRepo = new();
    private readonly Mock<IMemberRepository> _memberRepo = new();
    private readonly Mock<IWorkerDispatcher> _workerDispatcher = new();
    private readonly PhaseProgressionService _sut;

    public PhaseProgressionServiceTests()
    {
        _workerDispatcher.Setup(x => x.DispatchJobAsync(It.IsAny<WorkerJobMessage>()))
            .ReturnsAsync((WorkerJobMessage job) => job.JobId);
        _stepRepo.Setup(x => x.SetDispatchedAsync(It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        _sut = new PhaseProgressionService(
            _stepRepo.Object,
            _phaseRepo.Object,
            _batchRepo.Object,
            _memberRepo.Object,
            _workerDispatcher.Object,
            Mock.Of<ILogger<PhaseProgressionService>>());
    }

    // ---- CheckMemberProgressionAsync ----

    [Fact]
    public async Task CheckMemberProgression_DispatchesNextPendingStep_AfterSucceeded()
    {
        // Member A has step[0]=succeeded, step[1]=pending
        var steps = new List<StepExecutionRecord>
        {
            Step(1, phaseId: 10, memberId: 100, index: 0, status: StepStatus.Succeeded),
            Step(2, phaseId: 10, memberId: 100, index: 1, status: StepStatus.Pending,
                 workerId: "w1", functionName: "fn1")
        };
        _stepRepo.Setup(x => x.GetByPhaseAndMemberAsync(10, 100)).ReturnsAsync(steps);
        _phaseRepo.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(Phase(10, batchId: 1));

        await _sut.CheckMemberProgressionAsync(10, 100, "runbook1", 1);

        _workerDispatcher.Verify(x => x.DispatchJobAsync(It.Is<WorkerJobMessage>(j =>
            j.JobId == "step-2" && j.WorkerId == "w1" && j.FunctionName == "fn1")), Times.Once);
        _stepRepo.Verify(x => x.SetDispatchedAsync(2, "step-2"), Times.Once);
    }

    [Fact]
    public async Task CheckMemberProgression_DoesNotWaitForOtherMembers()
    {
        // Member A step[0]=succeeded → should dispatch step[1] without waiting for member B
        var memberASteps = new List<StepExecutionRecord>
        {
            Step(1, phaseId: 10, memberId: 100, index: 0, status: StepStatus.Succeeded),
            Step(2, phaseId: 10, memberId: 100, index: 1, status: StepStatus.Pending,
                 workerId: "w1", functionName: "fn1")
        };
        _stepRepo.Setup(x => x.GetByPhaseAndMemberAsync(10, 100)).ReturnsAsync(memberASteps);
        _phaseRepo.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(Phase(10, batchId: 1));

        await _sut.CheckMemberProgressionAsync(10, 100, "runbook1", 1);

        _workerDispatcher.Verify(x => x.DispatchJobAsync(It.IsAny<WorkerJobMessage>()), Times.Once);
    }

    [Fact]
    public async Task CheckMemberProgression_WaitsForInProgressStep()
    {
        var steps = new List<StepExecutionRecord>
        {
            Step(1, phaseId: 10, memberId: 100, index: 0, status: StepStatus.Dispatched),
            Step(2, phaseId: 10, memberId: 100, index: 1, status: StepStatus.Pending)
        };
        _stepRepo.Setup(x => x.GetByPhaseAndMemberAsync(10, 100)).ReturnsAsync(steps);

        await _sut.CheckMemberProgressionAsync(10, 100, "runbook1", 1);

        _workerDispatcher.Verify(x => x.DispatchJobAsync(It.IsAny<WorkerJobMessage>()), Times.Never);
    }

    [Fact]
    public async Task CheckMemberProgression_WaitsForPollingStep()
    {
        var steps = new List<StepExecutionRecord>
        {
            Step(1, phaseId: 10, memberId: 100, index: 0, status: StepStatus.Polling),
            Step(2, phaseId: 10, memberId: 100, index: 1, status: StepStatus.Pending)
        };
        _stepRepo.Setup(x => x.GetByPhaseAndMemberAsync(10, 100)).ReturnsAsync(steps);

        await _sut.CheckMemberProgressionAsync(10, 100, "runbook1", 1);

        _workerDispatcher.Verify(x => x.DispatchJobAsync(It.IsAny<WorkerJobMessage>()), Times.Never);
    }

    [Fact]
    public async Task CheckMemberProgression_StopsOnFailedStep()
    {
        var steps = new List<StepExecutionRecord>
        {
            Step(1, phaseId: 10, memberId: 100, index: 0, status: StepStatus.Failed),
            Step(2, phaseId: 10, memberId: 100, index: 1, status: StepStatus.Pending)
        };
        _stepRepo.Setup(x => x.GetByPhaseAndMemberAsync(10, 100)).ReturnsAsync(steps);

        await _sut.CheckMemberProgressionAsync(10, 100, "runbook1", 1);

        _workerDispatcher.Verify(x => x.DispatchJobAsync(It.IsAny<WorkerJobMessage>()), Times.Never);
    }

    [Fact]
    public async Task CheckMemberProgression_AllSucceeded_ChecksPhaseCompletion()
    {
        var steps = new List<StepExecutionRecord>
        {
            Step(1, phaseId: 10, memberId: 100, index: 0, status: StepStatus.Succeeded),
            Step(2, phaseId: 10, memberId: 100, index: 1, status: StepStatus.Succeeded)
        };
        _stepRepo.Setup(x => x.GetByPhaseAndMemberAsync(10, 100)).ReturnsAsync(steps);

        // For CheckPhaseCompletionAsync
        _stepRepo.Setup(x => x.GetByPhaseExecutionAsync(10)).ReturnsAsync(steps);
        _phaseRepo.Setup(x => x.SetCompletedAsync(10)).ReturnsAsync(true);
        _phaseRepo.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(Phase(10, batchId: 1));
        _phaseRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(new[] { Phase(10, batchId: 1, status: PhaseStatus.Completed) });
        _batchRepo.Setup(x => x.SetCompletedAsync(1)).ReturnsAsync(true);

        await _sut.CheckMemberProgressionAsync(10, 100, "runbook1", 1);

        _phaseRepo.Verify(x => x.SetCompletedAsync(10), Times.Once);
    }

    // ---- HandleMemberFailureAsync ----

    [Fact]
    public async Task HandleMemberFailure_MarksMemberFailed()
    {
        _memberRepo.Setup(x => x.SetFailedAsync(100)).ReturnsAsync(true);
        _stepRepo.Setup(x => x.GetPendingByMemberAsync(100)).ReturnsAsync(new List<StepExecutionRecord>());
        _stepRepo.Setup(x => x.GetByPhaseExecutionAsync(10)).ReturnsAsync(new List<StepExecutionRecord>());
        _phaseRepo.Setup(x => x.SetCompletedAsync(10)).ReturnsAsync(true);
        _phaseRepo.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(Phase(10, batchId: 1));
        _phaseRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(new[] { Phase(10, batchId: 1) });

        await _sut.HandleMemberFailureAsync(10, 100);

        _memberRepo.Verify(x => x.SetFailedAsync(100), Times.Once);
    }

    [Fact]
    public async Task HandleMemberFailure_CancelsAllPendingStepsAcrossPhases()
    {
        // Member has pending steps in current phase and a future phase
        var pendingSteps = new List<StepExecutionRecord>
        {
            Step(3, phaseId: 10, memberId: 100, index: 1, status: StepStatus.Pending),
            Step(5, phaseId: 20, memberId: 100, index: 0, status: StepStatus.Pending),
            Step(6, phaseId: 20, memberId: 100, index: 1, status: StepStatus.Dispatched)
        };

        _memberRepo.Setup(x => x.SetFailedAsync(100)).ReturnsAsync(true);
        _stepRepo.Setup(x => x.GetPendingByMemberAsync(100)).ReturnsAsync(pendingSteps);
        _stepRepo.Setup(x => x.SetCancelledAsync(It.IsAny<int>())).ReturnsAsync(true);
        _stepRepo.Setup(x => x.GetByPhaseExecutionAsync(10)).ReturnsAsync(new List<StepExecutionRecord>());
        _phaseRepo.Setup(x => x.SetCompletedAsync(10)).ReturnsAsync(true);
        _phaseRepo.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(Phase(10, batchId: 1));
        _phaseRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(new[] { Phase(10, batchId: 1) });

        await _sut.HandleMemberFailureAsync(10, 100);

        _stepRepo.Verify(x => x.SetCancelledAsync(3), Times.Once);
        _stepRepo.Verify(x => x.SetCancelledAsync(5), Times.Once);
        _stepRepo.Verify(x => x.SetCancelledAsync(6), Times.Once);
    }

    [Fact]
    public async Task HandleMemberFailure_IsIdempotent_WhenMemberAlreadyFailed()
    {
        // SetFailedAsync returns false (already failed)
        _memberRepo.Setup(x => x.SetFailedAsync(100)).ReturnsAsync(false);
        _stepRepo.Setup(x => x.GetPendingByMemberAsync(100)).ReturnsAsync(new List<StepExecutionRecord>());
        _stepRepo.Setup(x => x.GetByPhaseExecutionAsync(10)).ReturnsAsync(new List<StepExecutionRecord>());
        _phaseRepo.Setup(x => x.SetCompletedAsync(10)).ReturnsAsync(true);
        _phaseRepo.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(Phase(10, batchId: 1));
        _phaseRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(new[] { Phase(10, batchId: 1) });

        // Should not throw
        await _sut.HandleMemberFailureAsync(10, 100);

        _memberRepo.Verify(x => x.SetFailedAsync(100), Times.Once);
    }

    // ---- CheckPhaseCompletionAsync ----

    [Fact]
    public async Task CheckPhaseCompletion_WhenAllMembersSucceeded_MarksPhaseCompleted()
    {
        var allSteps = new List<StepExecutionRecord>
        {
            Step(1, phaseId: 10, memberId: 100, index: 0, status: StepStatus.Succeeded),
            Step(2, phaseId: 10, memberId: 100, index: 1, status: StepStatus.Succeeded),
            Step(3, phaseId: 10, memberId: 200, index: 0, status: StepStatus.Succeeded),
            Step(4, phaseId: 10, memberId: 200, index: 1, status: StepStatus.Succeeded)
        };

        _stepRepo.Setup(x => x.GetByPhaseExecutionAsync(10)).ReturnsAsync(allSteps);
        _phaseRepo.Setup(x => x.SetCompletedAsync(10)).ReturnsAsync(true);
        _phaseRepo.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(Phase(10, batchId: 1));
        _phaseRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(new[] { Phase(10, batchId: 1, status: PhaseStatus.Completed) });
        _batchRepo.Setup(x => x.SetCompletedAsync(1)).ReturnsAsync(true);

        await _sut.CheckPhaseCompletionAsync(10);

        _phaseRepo.Verify(x => x.SetCompletedAsync(10), Times.Once);
        _phaseRepo.Verify(x => x.SetFailedAsync(10), Times.Never);
    }

    [Fact]
    public async Task CheckPhaseCompletion_WhenAllMembersFailed_MarksPhaseFailed()
    {
        var allSteps = new List<StepExecutionRecord>
        {
            Step(1, phaseId: 10, memberId: 100, index: 0, status: StepStatus.Failed),
            Step(2, phaseId: 10, memberId: 100, index: 1, status: StepStatus.Cancelled),
            Step(3, phaseId: 10, memberId: 200, index: 0, status: StepStatus.PollTimeout),
            Step(4, phaseId: 10, memberId: 200, index: 1, status: StepStatus.Cancelled)
        };

        _stepRepo.Setup(x => x.GetByPhaseExecutionAsync(10)).ReturnsAsync(allSteps);
        _phaseRepo.Setup(x => x.SetFailedAsync(10)).ReturnsAsync(true);
        _phaseRepo.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(Phase(10, batchId: 1));
        _phaseRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(new[] { Phase(10, batchId: 1, status: PhaseStatus.Failed) });
        _batchRepo.Setup(x => x.SetFailedAsync(1)).ReturnsAsync(true);

        await _sut.CheckPhaseCompletionAsync(10);

        _phaseRepo.Verify(x => x.SetFailedAsync(10), Times.Once);
        _phaseRepo.Verify(x => x.SetCompletedAsync(10), Times.Never);
    }

    [Fact]
    public async Task CheckPhaseCompletion_MixedResults_MarksPhaseCompleted()
    {
        // Member 100 succeeded all steps, member 200 failed
        var allSteps = new List<StepExecutionRecord>
        {
            Step(1, phaseId: 10, memberId: 100, index: 0, status: StepStatus.Succeeded),
            Step(2, phaseId: 10, memberId: 100, index: 1, status: StepStatus.Succeeded),
            Step(3, phaseId: 10, memberId: 200, index: 0, status: StepStatus.Failed),
            Step(4, phaseId: 10, memberId: 200, index: 1, status: StepStatus.Cancelled)
        };

        _stepRepo.Setup(x => x.GetByPhaseExecutionAsync(10)).ReturnsAsync(allSteps);
        _phaseRepo.Setup(x => x.SetCompletedAsync(10)).ReturnsAsync(true);
        _phaseRepo.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(Phase(10, batchId: 1));
        _phaseRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(new[] { Phase(10, batchId: 1, status: PhaseStatus.Completed) });
        _batchRepo.Setup(x => x.SetCompletedAsync(1)).ReturnsAsync(true);

        await _sut.CheckPhaseCompletionAsync(10);

        _phaseRepo.Verify(x => x.SetCompletedAsync(10), Times.Once);
    }

    [Fact]
    public async Task CheckPhaseCompletion_StepsStillInProgress_DoesNothing()
    {
        var allSteps = new List<StepExecutionRecord>
        {
            Step(1, phaseId: 10, memberId: 100, index: 0, status: StepStatus.Succeeded),
            Step(2, phaseId: 10, memberId: 200, index: 0, status: StepStatus.Dispatched)
        };

        _stepRepo.Setup(x => x.GetByPhaseExecutionAsync(10)).ReturnsAsync(allSteps);

        await _sut.CheckPhaseCompletionAsync(10);

        _phaseRepo.Verify(x => x.SetCompletedAsync(It.IsAny<int>()), Times.Never);
        _phaseRepo.Verify(x => x.SetFailedAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task CheckPhaseCompletion_EmptyPhase_MarksCompleted()
    {
        _stepRepo.Setup(x => x.GetByPhaseExecutionAsync(10)).ReturnsAsync(new List<StepExecutionRecord>());
        _phaseRepo.Setup(x => x.SetCompletedAsync(10)).ReturnsAsync(true);
        _phaseRepo.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(Phase(10, batchId: 1));
        _phaseRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(new[] { Phase(10, batchId: 1, status: PhaseStatus.Completed) });
        _batchRepo.Setup(x => x.SetCompletedAsync(1)).ReturnsAsync(true);

        await _sut.CheckPhaseCompletionAsync(10);

        _phaseRepo.Verify(x => x.SetCompletedAsync(10), Times.Once);
    }

    // ---- CheckBatchCompletionAsync ----

    [Fact]
    public async Task CheckBatchCompletion_AllPhasesCompleted_MarksBatchCompleted()
    {
        var phases = new List<PhaseExecutionRecord>
        {
            Phase(10, batchId: 1, status: PhaseStatus.Completed),
            Phase(20, batchId: 1, status: PhaseStatus.Completed)
        };
        _phaseRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(phases);
        _batchRepo.Setup(x => x.SetCompletedAsync(1)).ReturnsAsync(true);

        await _sut.CheckBatchCompletionAsync(1);

        _batchRepo.Verify(x => x.SetCompletedAsync(1), Times.Once);
        _batchRepo.Verify(x => x.SetFailedAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task CheckBatchCompletion_SomePhasesCompletedSomeFailed_MarksBatchCompleted()
    {
        var phases = new List<PhaseExecutionRecord>
        {
            Phase(10, batchId: 1, status: PhaseStatus.Completed),
            Phase(20, batchId: 1, status: PhaseStatus.Failed)
        };
        _phaseRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(phases);
        _batchRepo.Setup(x => x.SetCompletedAsync(1)).ReturnsAsync(true);

        await _sut.CheckBatchCompletionAsync(1);

        _batchRepo.Verify(x => x.SetCompletedAsync(1), Times.Once);
    }

    [Fact]
    public async Task CheckBatchCompletion_AllPhasesFailed_MarksBatchFailed()
    {
        var phases = new List<PhaseExecutionRecord>
        {
            Phase(10, batchId: 1, status: PhaseStatus.Failed),
            Phase(20, batchId: 1, status: PhaseStatus.Failed)
        };
        _phaseRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(phases);
        _batchRepo.Setup(x => x.SetFailedAsync(1)).ReturnsAsync(true);

        await _sut.CheckBatchCompletionAsync(1);

        _batchRepo.Verify(x => x.SetFailedAsync(1), Times.Once);
        _batchRepo.Verify(x => x.SetCompletedAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task CheckBatchCompletion_PhaseStillPending_DoesNothing()
    {
        var phases = new List<PhaseExecutionRecord>
        {
            Phase(10, batchId: 1, status: PhaseStatus.Completed),
            Phase(20, batchId: 1, status: PhaseStatus.Pending)
        };
        _phaseRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(phases);

        await _sut.CheckBatchCompletionAsync(1);

        _batchRepo.Verify(x => x.SetCompletedAsync(It.IsAny<int>()), Times.Never);
        _batchRepo.Verify(x => x.SetFailedAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task CheckBatchCompletion_PhaseStillDispatched_DoesNothing()
    {
        var phases = new List<PhaseExecutionRecord>
        {
            Phase(10, batchId: 1, status: PhaseStatus.Completed),
            Phase(20, batchId: 1, status: PhaseStatus.Dispatched)
        };
        _phaseRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(phases);

        await _sut.CheckBatchCompletionAsync(1);

        _batchRepo.Verify(x => x.SetCompletedAsync(It.IsAny<int>()), Times.Never);
        _batchRepo.Verify(x => x.SetFailedAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task CheckBatchCompletion_SkippedAndCompletedPhases_MarksBatchCompleted()
    {
        var phases = new List<PhaseExecutionRecord>
        {
            Phase(10, batchId: 1, status: PhaseStatus.Completed),
            Phase(20, batchId: 1, status: PhaseStatus.Skipped)
        };
        _phaseRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(phases);
        _batchRepo.Setup(x => x.SetCompletedAsync(1)).ReturnsAsync(true);

        await _sut.CheckBatchCompletionAsync(1);

        _batchRepo.Verify(x => x.SetCompletedAsync(1), Times.Once);
    }

    [Fact]
    public async Task CheckBatchCompletion_AllSkippedAndSuperseded_MarksBatchFailed()
    {
        // No phase completed — all skipped/superseded — counts as failed
        var phases = new List<PhaseExecutionRecord>
        {
            Phase(10, batchId: 1, status: PhaseStatus.Skipped),
            Phase(20, batchId: 1, status: PhaseStatus.Superseded)
        };
        _phaseRepo.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(phases);
        _batchRepo.Setup(x => x.SetFailedAsync(1)).ReturnsAsync(true);

        await _sut.CheckBatchCompletionAsync(1);

        _batchRepo.Verify(x => x.SetFailedAsync(1), Times.Once);
    }

    // ---- Helpers ----

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

    private static PhaseExecutionRecord Phase(int id, int batchId, string status = PhaseStatus.Dispatched) =>
        new()
        {
            Id = id,
            BatchId = batchId,
            Status = status,
            PhaseName = $"phase-{id}"
        };
}
