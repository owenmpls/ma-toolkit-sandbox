using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scheduler.Functions.Settings;

namespace Scheduler.Functions.Services;

public interface IDispatchTimeoutChecker
{
    Task CheckDispatchedStepsAsync(DateTime now);
}

public class DispatchTimeoutChecker : IDispatchTimeoutChecker
{
    private readonly IStepExecutionRepository _stepRepo;
    private readonly IInitExecutionRepository _initRepo;
    private readonly IServiceBusPublisher _publisher;
    private readonly IDbConnectionFactory _db;
    private readonly int _timeoutMinutes;
    private readonly ILogger<DispatchTimeoutChecker> _logger;

    public DispatchTimeoutChecker(
        IStepExecutionRepository stepRepo,
        IInitExecutionRepository initRepo,
        IServiceBusPublisher publisher,
        IDbConnectionFactory db,
        IOptions<SchedulerSettings> settings,
        ILogger<DispatchTimeoutChecker> logger)
    {
        _stepRepo = stepRepo;
        _initRepo = initRepo;
        _publisher = publisher;
        _db = db;
        _timeoutMinutes = settings.Value.DispatchTimeoutMinutes;
        _logger = logger;
    }

    public async Task CheckDispatchedStepsAsync(DateTime now)
    {
        var cutoff = now.AddMinutes(-_timeoutMinutes);
        var stuckSteps = await _stepRepo.GetDispatchedStepsOlderThanAsync(cutoff);
        foreach (var step in stuckSteps)
        {
            using var conn = _db.CreateConnection();
            var batchInfo = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<dynamic>(conn, @"
                SELECT b.id AS BatchId, r.name AS RunbookName, r.version AS RunbookVersion
                FROM step_executions se
                JOIN phase_executions pe ON se.phase_execution_id = pe.id
                JOIN batches b ON pe.batch_id = b.id
                JOIN runbooks r ON b.runbook_id = r.id AND r.is_active = 1
                WHERE se.id = @StepId",
                new { StepId = step.Id });

            if (batchInfo is null) continue;

            await _publisher.PublishDispatchTimeoutAsync(new DispatchTimeoutMessage
            {
                StepExecutionId = step.Id,
                IsInitStep = false,
                RunbookName = (string)batchInfo.RunbookName,
                RunbookVersion = (int)batchInfo.RunbookVersion,
                BatchId = (int)batchInfo.BatchId,
                StepName = step.StepName,
                DispatchedAt = step.DispatchedAt!.Value
            });

            _logger.LogWarning(
                "Detected dispatch timeout for step execution {StepId} (dispatched at {DispatchedAt})",
                step.Id, step.DispatchedAt);
        }

        var stuckInits = await _initRepo.GetDispatchedStepsOlderThanAsync(cutoff);
        foreach (var init in stuckInits)
        {
            using var conn = _db.CreateConnection();
            var batchInfo = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<dynamic>(conn, @"
                SELECT b.id AS BatchId, r.name AS RunbookName, r.version AS RunbookVersion
                FROM init_executions ie
                JOIN batches b ON ie.batch_id = b.id
                JOIN runbooks r ON b.runbook_id = r.id AND r.is_active = 1
                WHERE ie.id = @InitId",
                new { InitId = init.Id });

            if (batchInfo is null) continue;

            await _publisher.PublishDispatchTimeoutAsync(new DispatchTimeoutMessage
            {
                StepExecutionId = init.Id,
                IsInitStep = true,
                RunbookName = (string)batchInfo.RunbookName,
                RunbookVersion = (int)batchInfo.RunbookVersion,
                BatchId = (int)batchInfo.BatchId,
                StepName = init.StepName,
                DispatchedAt = init.DispatchedAt!.Value
            });

            _logger.LogWarning(
                "Detected dispatch timeout for init execution {InitId} (dispatched at {DispatchedAt})",
                init.Id, init.DispatchedAt);
        }
    }
}
