using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Services;
using Microsoft.Extensions.Logging;

namespace Scheduler.Functions.Services;

public interface IPollingManager
{
    Task CheckPollingStepsAsync(DateTime now);
}

public class PollingManager : IPollingManager
{
    private readonly IStepExecutionRepository _stepRepo;
    private readonly IInitExecutionRepository _initRepo;
    private readonly IServiceBusPublisher _publisher;
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<PollingManager> _logger;

    public PollingManager(
        IStepExecutionRepository stepRepo,
        IInitExecutionRepository initRepo,
        IServiceBusPublisher publisher,
        IDbConnectionFactory db,
        ILogger<PollingManager> logger)
    {
        _stepRepo = stepRepo;
        _initRepo = initRepo;
        _publisher = publisher;
        _db = db;
        _logger = logger;
    }

    public async Task CheckPollingStepsAsync(DateTime now)
    {
        // Check step_executions with polling due
        var pollingSteps = await _stepRepo.GetPollingStepsDueAsync(now);
        foreach (var step in pollingSteps)
        {
            // Load batch info from step's phase execution
            using var conn = _db.CreateConnection();
            var batchInfo = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<dynamic>(conn, @"
                SELECT b.id AS BatchId, b.runbook_id AS RunbookId, r.name AS RunbookName, r.version AS RunbookVersion
                FROM step_executions se
                JOIN phase_executions pe ON se.phase_execution_id = pe.id
                JOIN batches b ON pe.batch_id = b.id
                JOIN runbooks r ON b.runbook_id = r.id AND r.is_active = 1
                WHERE se.id = @StepId",
                new { StepId = step.Id });

            if (batchInfo is null) continue;

            await _publisher.PublishPollCheckAsync(new PollCheckMessage
            {
                RunbookName = (string)batchInfo.RunbookName,
                RunbookVersion = (int)batchInfo.RunbookVersion,
                BatchId = (int)batchInfo.BatchId,
                StepExecutionId = step.Id,
                StepName = step.StepName,
                PollCount = step.PollCount + 1,
                IsInitStep = false
            });

            await _stepRepo.UpdateLastPolledAsync(step.Id);

            _logger.LogDebug("Published poll-check for step execution {StepId}", step.Id);
        }

        // Check init_executions with polling due
        var pollingInits = await _initRepo.GetPollingStepsDueAsync(now);
        foreach (var init in pollingInits)
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

            await _publisher.PublishPollCheckAsync(new PollCheckMessage
            {
                RunbookName = (string)batchInfo.RunbookName,
                RunbookVersion = (int)batchInfo.RunbookVersion,
                BatchId = (int)batchInfo.BatchId,
                StepExecutionId = init.Id,
                StepName = init.StepName,
                PollCount = init.PollCount + 1,
                IsInitStep = true
            });

            await _initRepo.UpdateLastPolledAsync(init.Id);

            _logger.LogDebug("Published poll-check for init execution {InitId}", init.Id);
        }
    }
}
