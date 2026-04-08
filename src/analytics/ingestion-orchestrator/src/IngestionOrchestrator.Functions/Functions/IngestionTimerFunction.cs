using Cronos;
using IngestionOrchestrator.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace IngestionOrchestrator.Functions.Functions;

public class IngestionTimerFunction
{
    // Track last dispatch time per job to prevent double-dispatch within the same
    // cron window. Static so it survives across function invocations on the same instance.
    private static readonly Dictionary<string, DateTime> _lastDispatched = new();

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

        foreach (var job in _configLoader.Jobs.Jobs)
        {
            if (!job.Enabled || string.IsNullOrEmpty(job.Cron))
                continue;

            try
            {
                var cron = CronExpression.Parse(job.Cron);

                // Use the last dispatch time (or 2 minutes ago on first run) as the
                // baseline. GetNextOccurrence returns the next time AFTER the baseline,
                // so a job will only match once per cron occurrence.
                var lastRun = _lastDispatched.GetValueOrDefault(job.Name, now.AddMinutes(-2));
                var nextOccurrence = cron.GetNextOccurrence(lastRun);

                if (nextOccurrence == null || nextOccurrence > now)
                    continue;

                _logger.LogInformation("Job {Job} is due, dispatching", job.Name);
                await _runExecutor.ExecuteAsync(job, "scheduled", triggeredBy: null);
                _lastDispatched[job.Name] = now;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating job {Job}", job.Name);
            }
        }
    }
}
