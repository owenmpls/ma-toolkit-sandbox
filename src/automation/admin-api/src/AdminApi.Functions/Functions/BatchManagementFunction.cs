using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AdminApi.Functions.Services;
using AdminApi.Functions.Services.Repositories;

namespace AdminApi.Functions.Functions;

public class BatchManagementFunction
{
    private readonly IBatchRepository _batchRepo;
    private readonly IRunbookRepository _runbookRepo;
    private readonly IPhaseExecutionRepository _phaseRepo;
    private readonly IStepExecutionRepository _stepRepo;
    private readonly IInitExecutionRepository _initRepo;
    private readonly IRunbookParser _parser;
    private readonly ICsvUploadService _csvUpload;
    private readonly IManualBatchService _manualBatch;
    private readonly ILogger<BatchManagementFunction> _logger;

    public BatchManagementFunction(
        IBatchRepository batchRepo,
        IRunbookRepository runbookRepo,
        IPhaseExecutionRepository phaseRepo,
        IStepExecutionRepository stepRepo,
        IInitExecutionRepository initRepo,
        IRunbookParser parser,
        ICsvUploadService csvUpload,
        IManualBatchService manualBatch,
        ILogger<BatchManagementFunction> logger)
    {
        _batchRepo = batchRepo;
        _runbookRepo = runbookRepo;
        _phaseRepo = phaseRepo;
        _stepRepo = stepRepo;
        _initRepo = initRepo;
        _parser = parser;
        _csvUpload = csvUpload;
        _manualBatch = manualBatch;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/batches - List batches with optional filters
    /// </summary>
    [Function("ListBatches")]
    public async Task<IActionResult> ListAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "batches")] HttpRequest req)
    {
        _logger.LogInformation("ListBatches request");

        // Parse query parameters
        int? runbookId = req.Query.TryGetValue("runbookId", out var rbId) && int.TryParse(rbId, out var rbIdVal)
            ? rbIdVal : null;
        string? status = req.Query.TryGetValue("status", out var st) ? st.ToString() : null;
        bool? isManual = req.Query.TryGetValue("manual", out var manual) && bool.TryParse(manual, out var manualVal)
            ? manualVal : null;

        var batches = await _batchRepo.ListAsync(runbookId, status, isManual);

        return new OkObjectResult(new
        {
            batches = batches.Select(b => new
            {
                b.Id,
                b.RunbookId,
                b.BatchStartTime,
                b.Status,
                b.IsManual,
                b.CreatedBy,
                b.CurrentPhase,
                b.DetectedAt,
                b.InitDispatchedAt
            })
        });
    }

    /// <summary>
    /// GET /api/batches/{id} - Get batch details
    /// </summary>
    [Function("GetBatch")]
    public async Task<IActionResult> GetAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "batches/{id:int}")] HttpRequest req,
        int id)
    {
        _logger.LogInformation("GetBatch request for {BatchId}", id);

        var batch = await _batchRepo.GetByIdAsync(id);
        if (batch is null)
            return new NotFoundObjectResult(new { error = $"Batch {id} not found" });

        var phases = await _phaseRepo.GetByBatchAsync(id);
        var initExecs = await _initRepo.GetByBatchAsync(id);

        return new OkObjectResult(new
        {
            batch = new
            {
                batch.Id,
                batch.RunbookId,
                batch.BatchStartTime,
                batch.Status,
                batch.IsManual,
                batch.CreatedBy,
                batch.CurrentPhase,
                batch.DetectedAt,
                batch.InitDispatchedAt
            },
            phases = phases.OrderBy(p => p.OffsetMinutes).Select(p => new
            {
                p.Id,
                p.PhaseName,
                p.OffsetMinutes,
                p.DueAt,
                p.Status,
                p.DispatchedAt,
                p.CompletedAt
            }),
            initSteps = initExecs.OrderBy(i => i.StepIndex).Select(i => new
            {
                i.Id,
                i.StepName,
                i.StepIndex,
                i.Status,
                i.DispatchedAt,
                i.CompletedAt
            })
        });
    }

    /// <summary>
    /// POST /api/batches - Create manual batch from CSV upload
    /// Expects multipart/form-data with:
    /// - runbookName: string
    /// - file: CSV file
    /// </summary>
    [Function("CreateBatch")]
    public async Task<IActionResult> CreateAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "batches")] HttpRequest req)
    {
        _logger.LogInformation("CreateBatch request");

        // Get runbook name from form
        if (!req.HasFormContentType)
            return new BadRequestObjectResult(new { error = "Request must be multipart/form-data" });

        var form = await req.ReadFormAsync();

        if (!form.TryGetValue("runbookName", out var runbookNameValues) || string.IsNullOrEmpty(runbookNameValues))
            return new BadRequestObjectResult(new { error = "runbookName is required" });

        var runbookName = runbookNameValues.ToString();

        // Get runbook
        var runbook = await _runbookRepo.GetByNameAsync(runbookName);
        if (runbook is null)
            return new NotFoundObjectResult(new { error = $"Runbook '{runbookName}' not found" });

        // Parse YAML
        var definition = _parser.Parse(runbook.YamlContent);

        // Get CSV file
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            return new BadRequestObjectResult(new { error = "CSV file is required" });

        // Parse CSV
        await using var stream = file.OpenReadStream();
        var csvResult = await _csvUpload.ParseCsvAsync(stream, definition);

        if (!csvResult.Success)
        {
            return new BadRequestObjectResult(new
            {
                error = "CSV validation failed",
                errors = csvResult.Errors,
                warnings = csvResult.Warnings
            });
        }

        // Create batch
        // TODO: Get user from auth context when EasyAuth is implemented
        var createdBy = "system";

        var result = await _manualBatch.CreateBatchAsync(runbook, definition, csvResult.Data!, createdBy);

        if (!result.Success)
        {
            return new BadRequestObjectResult(new { error = result.ErrorMessage });
        }

        return new OkObjectResult(new
        {
            batchId = result.BatchId,
            status = result.Status,
            memberCount = result.MemberCount,
            availablePhases = result.AvailablePhases,
            warnings = csvResult.Warnings
        });
    }

    /// <summary>
    /// GET /api/batches/{id}/phases - List phase executions for a batch
    /// </summary>
    [Function("ListBatchPhases")]
    public async Task<IActionResult> ListPhasesAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "batches/{id:int}/phases")] HttpRequest req,
        int id)
    {
        _logger.LogInformation("ListBatchPhases request for {BatchId}", id);

        var batch = await _batchRepo.GetByIdAsync(id);
        if (batch is null)
            return new NotFoundObjectResult(new { error = $"Batch {id} not found" });

        var phases = await _phaseRepo.GetByBatchAsync(id);

        return new OkObjectResult(new
        {
            batchId = id,
            phases = phases.OrderBy(p => p.OffsetMinutes).Select(p => new
            {
                p.Id,
                p.PhaseName,
                p.OffsetMinutes,
                p.DueAt,
                p.Status,
                p.RunbookVersion,
                p.DispatchedAt,
                p.CompletedAt
            })
        });
    }

    /// <summary>
    /// GET /api/batches/{id}/steps - List step executions for a batch
    /// </summary>
    [Function("ListBatchSteps")]
    public async Task<IActionResult> ListStepsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "batches/{id:int}/steps")] HttpRequest req,
        int id)
    {
        _logger.LogInformation("ListBatchSteps request for {BatchId}", id);

        var batch = await _batchRepo.GetByIdAsync(id);
        if (batch is null)
            return new NotFoundObjectResult(new { error = $"Batch {id} not found" });

        var steps = await _stepRepo.GetByBatchAsync(id);

        return new OkObjectResult(new
        {
            batchId = id,
            steps = steps.Select(s => new
            {
                s.Id,
                s.PhaseExecutionId,
                s.BatchMemberId,
                s.StepName,
                s.StepIndex,
                s.WorkerId,
                s.FunctionName,
                s.Status,
                s.IsPollStep,
                s.PollCount,
                s.DispatchedAt,
                s.CompletedAt,
                s.ErrorMessage
            })
        });
    }

    /// <summary>
    /// POST /api/batches/{id}/advance - Advance manual batch to next phase
    /// </summary>
    [Function("AdvanceBatch")]
    public async Task<IActionResult> AdvanceAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "batches/{id:int}/advance")] HttpRequest req,
        int id)
    {
        _logger.LogInformation("AdvanceBatch request for {BatchId}", id);

        var batch = await _batchRepo.GetByIdAsync(id);
        if (batch is null)
            return new NotFoundObjectResult(new { error = $"Batch {id} not found" });

        if (!batch.IsManual)
            return new BadRequestObjectResult(new { error = "Only manual batches can be advanced via API" });

        // Get runbook
        // Note: We need to get the runbook from the batch's runbook_id
        var runbooks = await _runbookRepo.GetActiveRunbooksAsync();
        var runbook = runbooks.FirstOrDefault(r => r.Id == batch.RunbookId);
        if (runbook is null)
            return new BadRequestObjectResult(new { error = "Runbook not found or no longer active" });

        var definition = _parser.Parse(runbook.YamlContent);

        var result = await _manualBatch.AdvanceBatchAsync(batch, runbook, definition);

        if (!result.Success)
        {
            return new BadRequestObjectResult(new { error = result.ErrorMessage });
        }

        return new OkObjectResult(new
        {
            action = result.Action,
            phaseName = result.PhaseName,
            memberCount = result.MemberCount,
            stepCount = result.StepCount,
            nextPhase = result.NextPhase
        });
    }

    /// <summary>
    /// POST /api/batches/{id}/cancel - Cancel a batch
    /// </summary>
    [Function("CancelBatch")]
    public async Task<IActionResult> CancelAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "batches/{id:int}/cancel")] HttpRequest req,
        int id)
    {
        _logger.LogInformation("CancelBatch request for {BatchId}", id);

        var batch = await _batchRepo.GetByIdAsync(id);
        if (batch is null)
            return new NotFoundObjectResult(new { error = $"Batch {id} not found" });

        if (batch.Status is BatchStatus.Completed or BatchStatus.Failed)
            return new BadRequestObjectResult(new { error = $"Batch is already {batch.Status}" });

        await _batchRepo.UpdateStatusAsync(id, BatchStatus.Failed);

        _logger.LogInformation("Cancelled batch {BatchId}", id);

        return new OkObjectResult(new
        {
            message = $"Batch {id} has been cancelled",
            batchId = id,
            status = BatchStatus.Failed
        });
    }
}
