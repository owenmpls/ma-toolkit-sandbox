using System.Data;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MaToolkit.Automation.Shared.Services.Repositories;
using Scheduler.Functions.Services;

namespace Scheduler.Functions.Functions;

public class SchedulerTimerFunction
{
    private readonly IRunbookRepository _runbookRepo;
    private readonly IAutomationSettingsRepository _automationRepo;
    private readonly IBatchRepository _batchRepo;
    private readonly IDynamicTableManager _dynamicTableManager;
    private readonly IDataSourceQueryService _dataSourceQuery;
    private readonly IRunbookParser _parser;
    private readonly IBatchDetector _batchDetector;
    private readonly IPhaseDispatcher _phaseDispatcher;
    private readonly IVersionTransitionHandler _versionTransitionHandler;
    private readonly IPollingManager _pollingManager;
    private readonly IDistributedLock _distributedLock;
    private readonly ILogger<SchedulerTimerFunction> _logger;

    public SchedulerTimerFunction(
        IRunbookRepository runbookRepo,
        IAutomationSettingsRepository automationRepo,
        IBatchRepository batchRepo,
        IDynamicTableManager dynamicTableManager,
        IDataSourceQueryService dataSourceQuery,
        IRunbookParser parser,
        IBatchDetector batchDetector,
        IPhaseDispatcher phaseDispatcher,
        IVersionTransitionHandler versionTransitionHandler,
        IPollingManager pollingManager,
        IDistributedLock distributedLock,
        ILogger<SchedulerTimerFunction> logger)
    {
        _runbookRepo = runbookRepo;
        _automationRepo = automationRepo;
        _batchRepo = batchRepo;
        _dynamicTableManager = dynamicTableManager;
        _dataSourceQuery = dataSourceQuery;
        _parser = parser;
        _batchDetector = batchDetector;
        _phaseDispatcher = phaseDispatcher;
        _versionTransitionHandler = versionTransitionHandler;
        _pollingManager = pollingManager;
        _distributedLock = distributedLock;
        _logger = logger;
    }

    [Function("SchedulerTimer")]
    public async Task RunAsync([TimerTrigger("0 */5 * * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Scheduler timer triggered at {Time}", DateTime.UtcNow);

        await using var lockHandle = await _distributedLock.TryAcquireAsync("scheduler-timer");
        if (lockHandle is null)
        {
            _logger.LogWarning("Scheduler timer skipped — another instance is already running");
            return;
        }

        var runbooks = await _runbookRepo.GetActiveRunbooksAsync();

        foreach (var runbook in runbooks)
        {
            try
            {
                await ProcessRunbookAsync(runbook);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing runbook {RunbookName} v{Version}",
                    runbook.Name, runbook.Version);
            }
        }

        // Check polling steps across all runbooks
        await _pollingManager.CheckPollingStepsAsync(DateTime.UtcNow);

        _logger.LogInformation("Scheduler timer completed");
    }

    private async Task ProcessRunbookAsync(RunbookRecord runbook)
    {
        _logger.LogInformation("Processing runbook {RunbookName} v{Version}", runbook.Name, runbook.Version);

        // Parse YAML
        var definition = _parser.Parse(runbook.YamlContent);

        // Check automation settings - skip query execution if automation is disabled
        var automationSettings = await _automationRepo.GetByNameAsync(runbook.Name);
        var automationEnabled = automationSettings?.AutomationEnabled ?? false;

        var now = DateTime.UtcNow;

        if (automationEnabled)
        {
            // Execute data source query only when automation is enabled
            DataTable queryResults;
            try
            {
                queryResults = await _dataSourceQuery.ExecuteAsync(definition.DataSource);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute data source query for runbook {RunbookName}", runbook.Name);
                // Continue to process existing batches even if query fails
                await ProcessExistingBatchesAsync(runbook, definition, now);
                return;
            }

            if (queryResults.Rows.Count > 0)
            {
                // Ensure dynamic data table exists
                var queryColumns = queryResults.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
                await _dynamicTableManager.EnsureTableAsync(
                    runbook.DataTableName,
                    queryColumns,
                    definition.DataSource.MultiValuedColumns);

                // Upsert query results into dynamic table
                await _dynamicTableManager.UpsertDataAsync(
                    runbook.DataTableName,
                    definition.DataSource.PrimaryKey,
                    definition.DataSource.BatchTimeColumn,
                    queryResults,
                    definition.DataSource.MultiValuedColumns);

                // Group by batch time and process each group
                var batchGroups = await _batchDetector.GroupByBatchTimeAsync(queryResults, definition.DataSource);

                foreach (var (batchTime, rows) in batchGroups)
                {
                    await _batchDetector.ProcessBatchGroupAsync(runbook, definition, batchTime, rows, now);
                }
            }
            else
            {
                _logger.LogInformation("No results from data source for runbook {RunbookName}", runbook.Name);
            }
        }
        else
        {
            _logger.LogInformation(
                "Skipping query execution for runbook {RunbookName} - automation disabled",
                runbook.Name);
        }

        // ALWAYS process existing batches (including manual batches) regardless of automation setting
        await ProcessExistingBatchesAsync(runbook, definition, now);
    }

    private async Task ProcessExistingBatchesAsync(RunbookRecord runbook, RunbookDefinition definition, DateTime now)
    {
        // Evaluate pending phases and handle version transitions for all active batches
        var activeBatches = await _batchRepo.GetActiveByRunbookNameAsync(runbook.Name);
        foreach (var batch in activeBatches)
        {
            await _phaseDispatcher.EvaluatePendingPhasesAsync(runbook, definition, batch, now);
            await _versionTransitionHandler.HandleVersionTransitionAsync(runbook, definition, batch, now);
        }
    }
}
