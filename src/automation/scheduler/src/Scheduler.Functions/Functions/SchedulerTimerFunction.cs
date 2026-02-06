using System.Data;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Scheduler.Functions.Services;

namespace Scheduler.Functions.Functions;

public class SchedulerTimerFunction
{
    private readonly IRunbookRepository _runbookRepo;
    private readonly IBatchRepository _batchRepo;
    private readonly IDynamicTableManager _dynamicTableManager;
    private readonly IDataSourceQueryService _dataSourceQuery;
    private readonly IRunbookParser _parser;
    private readonly IBatchDetector _batchDetector;
    private readonly IPhaseDispatcher _phaseDispatcher;
    private readonly IVersionTransitionHandler _versionTransitionHandler;
    private readonly IPollingManager _pollingManager;
    private readonly ILogger<SchedulerTimerFunction> _logger;

    public SchedulerTimerFunction(
        IRunbookRepository runbookRepo,
        IBatchRepository batchRepo,
        IDynamicTableManager dynamicTableManager,
        IDataSourceQueryService dataSourceQuery,
        IRunbookParser parser,
        IBatchDetector batchDetector,
        IPhaseDispatcher phaseDispatcher,
        IVersionTransitionHandler versionTransitionHandler,
        IPollingManager pollingManager,
        ILogger<SchedulerTimerFunction> logger)
    {
        _runbookRepo = runbookRepo;
        _batchRepo = batchRepo;
        _dynamicTableManager = dynamicTableManager;
        _dataSourceQuery = dataSourceQuery;
        _parser = parser;
        _batchDetector = batchDetector;
        _phaseDispatcher = phaseDispatcher;
        _versionTransitionHandler = versionTransitionHandler;
        _pollingManager = pollingManager;
        _logger = logger;
    }

    [Function("SchedulerTimer")]
    public async Task RunAsync([TimerTrigger("0 */5 * * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Scheduler timer triggered at {Time}", DateTime.UtcNow);

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

        // Execute data source query
        DataTable queryResults;
        try
        {
            queryResults = await _dataSourceQuery.ExecuteAsync(definition.DataSource);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute data source query for runbook {RunbookName}", runbook.Name);
            return;
        }

        if (queryResults.Rows.Count == 0)
        {
            _logger.LogInformation("No results from data source for runbook {RunbookName}", runbook.Name);
            return;
        }

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

        var now = DateTime.UtcNow;

        foreach (var (batchTime, rows) in batchGroups)
        {
            await _batchDetector.ProcessBatchGroupAsync(runbook, definition, batchTime, rows, now);
        }

        // Evaluate pending phases and handle version transitions for all active batches
        var activeBatches = await _batchRepo.GetActiveByRunbookAsync(runbook.Id);
        foreach (var batch in activeBatches)
        {
            await _phaseDispatcher.EvaluatePendingPhasesAsync(runbook, definition, batch, now);
            await _versionTransitionHandler.HandleVersionTransitionAsync(runbook, definition, batch, now);
        }
    }
}
