using System.Data;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Exceptions;
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

    [Fact]
    public async Task CreateStepExecutions_UnresolvedTemplate_CreatesAllStepsWithRawParams()
    {
        // Setup: phase exists, but NO existing steps (triggers creation path)
        _phaseRepo.Setup(x => x.GetByIdAsync(10))
            .ReturnsAsync(new PhaseExecutionRecord { Id = 10, BatchId = 1, Status = PhaseStatus.Pending });

        // First call (creation check): empty → triggers creation
        // Second call (dispatch): return created steps
        var createdSteps = new List<StepExecutionRecord>
        {
            Step(1, phaseId: 10, memberId: 100, index: 0, status: StepStatus.Pending,
                 workerId: "w1", functionName: "Create-User"),
            Step(2, phaseId: 10, memberId: 100, index: 1, status: StepStatus.Pending,
                 workerId: "w1", functionName: "Set-License")
        };
        _stepRepo.SetupSequence(x => x.GetByPhaseExecutionAsync(10))
            .ReturnsAsync(new List<StepExecutionRecord>())  // creation check
            .ReturnsAsync(createdSteps);                     // dispatch query

        // Runbook + parser setup
        _runbookRepo.Setup(x => x.GetByNameAndVersionAsync("runbook1", 1))
            .ReturnsAsync(new RunbookRecord { Id = 1, Name = "runbook1", Version = 1, YamlContent = "yaml" });

        var step0Params = new Dictionary<string, string> { { "upn", "{{UserPrincipalName}}" } };
        var step1Params = new Dictionary<string, string> { { "userId", "{{CreatedUserId}}" } };
        var definition = new RunbookDefinition
        {
            Name = "runbook1",
            Phases = new List<PhaseDefinition>
            {
                new()
                {
                    Name = "phase1",
                    Steps = new List<StepDefinition>
                    {
                        new() { Name = "create-user", WorkerId = "w1", Function = "Create-User", Params = step0Params },
                        new() { Name = "set-license", WorkerId = "w1", Function = "Set-License", Params = step1Params }
                    }
                }
            }
        };
        _runbookParser.Setup(x => x.Parse("yaml")).Returns(definition);

        // Batch + members
        _batchRepo.Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(new BatchRecord { Id = 1 });
        _memberRepo.Setup(x => x.GetActiveByBatchAsync(1))
            .ReturnsAsync(new List<BatchMemberRecord>
            {
                new() { Id = 100, BatchId = 1, MemberKey = "user1",
                         DataJson = JsonSerializer.Serialize(new Dictionary<string, string> { { "UserPrincipalName", "user1@test.com" } }) }
            });

        // Mock DB connection + transaction
        var mockTransaction = new Mock<IDbTransaction>();
        var mockConnection = new Mock<IDbConnection>();
        mockConnection.Setup(c => c.BeginTransaction()).Returns(mockTransaction.Object);
        _db.Setup(d => d.CreateConnection()).Returns(mockConnection.Object);

        // Template resolver: step[0] resolves fine, step[1] throws (output_params not yet available)
        _templateResolver.Setup(x => x.ResolveParams(step0Params, It.IsAny<Dictionary<string, string>>(), 1, null))
            .Returns(new Dictionary<string, string> { { "upn", "user1@test.com" } });
        _templateResolver.Setup(x => x.ResolveString("Create-User", It.IsAny<Dictionary<string, string>>(), 1, null))
            .Returns("Create-User");
        _templateResolver.Setup(x => x.ResolveParams(step1Params, It.IsAny<Dictionary<string, string>>(), 1, null))
            .Throws(new TemplateResolutionException("{{CreatedUserId}}", new[] { "CreatedUserId" }));
        _templateResolver.Setup(x => x.ResolveString("Set-License", It.IsAny<Dictionary<string, string>>(), 1, null))
            .Returns("Set-License");

        _stepRepo.Setup(x => x.InsertAsync(It.IsAny<StepExecutionRecord>(), It.IsAny<IDbTransaction>()))
            .ReturnsAsync(1);

        var message = CreateMessage(10, 1, "phase1", "runbook1", 1);
        await _sut.HandleAsync(message);

        // Both steps should be created (2 InsertAsync calls), not just step[0]
        _stepRepo.Verify(x => x.InsertAsync(It.IsAny<StepExecutionRecord>(), It.IsAny<IDbTransaction>()),
            Times.Exactly(2));

        // Step[1] should have raw (unresolved) params stored
        _stepRepo.Verify(x => x.InsertAsync(
            It.Is<StepExecutionRecord>(s =>
                s.StepIndex == 1 &&
                s.ParamsJson!.Contains("{{CreatedUserId}}")),
            It.IsAny<IDbTransaction>()), Times.Once);
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
