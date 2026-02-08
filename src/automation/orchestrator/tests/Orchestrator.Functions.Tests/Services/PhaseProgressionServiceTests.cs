using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Services;
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
    private readonly Mock<IRunbookRepository> _runbookRepo = new();
    private readonly Mock<IRunbookParser> _runbookParser = new();
    private readonly Mock<ITemplateResolver> _templateResolver = new();
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
            _runbookRepo.Object,
            _runbookParser.Object,
            _templateResolver.Object,
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

    // ---- Re-resolution at dispatch time ----

    [Fact]
    public async Task CheckMemberProgression_ReResolvesParamsFromWorkerData()
    {
        var steps = new List<StepExecutionRecord>
        {
            Step(1, phaseId: 10, memberId: 100, index: 0, status: StepStatus.Succeeded),
            Step(2, phaseId: 10, memberId: 100, index: 1, status: StepStatus.Pending,
                 workerId: "w1", functionName: "Start-Migration")
        };
        _stepRepo.Setup(x => x.GetByPhaseAndMemberAsync(10, 100)).ReturnsAsync(steps);
        _phaseRepo.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(new PhaseExecutionRecord
        {
            Id = 10, BatchId = 1, PhaseName = "phase1"
        });

        // Member has data_json + worker_data_json (from step 0's output)
        _memberRepo.Setup(x => x.GetByIdAsync(100)).ReturnsAsync(new BatchMemberRecord
        {
            Id = 100, BatchId = 1, MemberKey = "user@test.com",
            DataJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["UserPrincipalName"] = "user@test.com"
            }),
            WorkerDataJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["MailboxGuid"] = "abc-123"
            })
        });

        // Runbook with step definition
        _runbookRepo.Setup(x => x.GetByNameAndVersionAsync("runbook1", 1)).ReturnsAsync(
            new RunbookRecord { Id = 1, Name = "runbook1", Version = 1, YamlContent = "yaml",
                DataTableName = "t" });
        var stepDef = new MaToolkit.Automation.Shared.Models.Yaml.StepDefinition
        {
            Name = "step-1",
            Function = "Start-Migration",
            Params = new Dictionary<string, string> { ["mailbox_guid"] = "{{MailboxGuid}}" }
        };
        var definition = new MaToolkit.Automation.Shared.Models.Yaml.RunbookDefinition
        {
            Phases = new List<MaToolkit.Automation.Shared.Models.Yaml.PhaseDefinition>
            {
                new() { Name = "phase1", Steps = new List<MaToolkit.Automation.Shared.Models.Yaml.StepDefinition>
                {
                    new() { Name = "step-0" }, stepDef
                }}
            }
        };
        _runbookParser.Setup(x => x.Parse(It.IsAny<string>())).Returns(definition);

        _batchRepo.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new BatchRecord { Id = 1 });
        _templateResolver.Setup(x => x.ResolveParams(
            It.IsAny<Dictionary<string, string>>(),
            It.Is<Dictionary<string, string>>(d => d.ContainsKey("MailboxGuid") && d["MailboxGuid"] == "abc-123"),
            It.IsAny<int>(), It.IsAny<DateTime?>()))
            .Returns(new Dictionary<string, string> { ["mailbox_guid"] = "abc-123" });
        _templateResolver.Setup(x => x.ResolveString(
            It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(),
            It.IsAny<int>(), It.IsAny<DateTime?>()))
            .Returns("Start-Migration");

        await _sut.CheckMemberProgressionAsync(10, 100, "runbook1", 1);

        // Verify re-resolved params are used in the dispatched job
        _workerDispatcher.Verify(x => x.DispatchJobAsync(It.Is<WorkerJobMessage>(j =>
            j.Parameters.ContainsKey("mailbox_guid") && j.Parameters["mailbox_guid"] == "abc-123")), Times.Once);
        _stepRepo.Verify(x => x.UpdateParamsJsonAsync(2, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task CheckMemberProgression_FallsBackToPreResolvedParams_WhenRunbookNotFound()
    {
        var steps = new List<StepExecutionRecord>
        {
            Step(1, phaseId: 10, memberId: 100, index: 0, status: StepStatus.Succeeded),
            Step(2, phaseId: 10, memberId: 100, index: 1, status: StepStatus.Pending,
                 workerId: "w1", functionName: "fn1")
        };
        steps[1].ParamsJson = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, string> { ["key"] = "pre-resolved-value" });

        _stepRepo.Setup(x => x.GetByPhaseAndMemberAsync(10, 100)).ReturnsAsync(steps);
        _phaseRepo.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(Phase(10, batchId: 1));

        _memberRepo.Setup(x => x.GetByIdAsync(100)).ReturnsAsync(new BatchMemberRecord
        {
            Id = 100, DataJson = "{\"k\":\"v\"}"
        });
        // Runbook not found
        _runbookRepo.Setup(x => x.GetByNameAndVersionAsync("runbook1", 1))
            .ReturnsAsync((RunbookRecord?)null);

        await _sut.CheckMemberProgressionAsync(10, 100, "runbook1", 1);

        // Should still dispatch with pre-resolved params
        _workerDispatcher.Verify(x => x.DispatchJobAsync(It.Is<WorkerJobMessage>(j =>
            j.Parameters.ContainsKey("key") && j.Parameters["key"] == "pre-resolved-value")), Times.Once);
        // Should not try to update params in DB
        _stepRepo.Verify(x => x.UpdateParamsJsonAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
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
