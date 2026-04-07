using Cronos;
using IngestionOrchestrator.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace IngestionOrchestrator.Functions.Functions;

public class IngestionTimerFunction
{
    private readonly IConfigLoader _configLoader;
    private readonly IRunExecutor _runExecutor;
    private readonly IRunTracker _runTracker;
    private readonly ILogger<IngestionTimerFunction> _logger;

    public IngestionTimerFunction(
        IConfigLoader configLoader,
        IRunExecutor runExecutor,
        IRunTracker runTracker,
        ILogger<IngestionTimerFunction> logger)
    {
        _configLoader = configLoader;
        _runExecutor = runExecutor;
        _runTracker = runTracker;
        _logger = logger;
    }

    [Function("IngestionTimer")]
    public async Task RunAsync(
        [TimerTrigger("0 * * * * *")] TimerInfo timer)
    {
        // 1. Check status of previously dispatched runs
        await _runTracker.CheckActiveRunsAsync();

        // 2. Evaluate cron schedules for new dispatches
        var now = DateTime.UtcNow;
        var windowStart = now.AddSeconds(-30);

        foreach (var job in _configLoader.Jobs.Jobs)
        {
            if (!job.Enabled || string.IsNullOrEmpty(job.Cron))
                continue;

            try
            {
                var cron = CronExpression.Parse(job.Cron);
                var nextOccurrence = cron.GetNextOccurrence(windowStart, inclusive: true);

                if (nextOccurrence == null || nextOccurrence > now)
                    continue;

                _logger.LogInformation("Job {Job} is due, dispatching", job.Name);
                await _runExecutor.ExecuteAsync(job, "scheduled", triggeredBy: null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating job {Job}", job.Name);
            }
        }
    }
}
