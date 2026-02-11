using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AdminApi.Functions.Auth;
using AdminApi.Functions.Services;
using MaToolkit.Automation.Shared.Services.Repositories;

namespace AdminApi.Functions.Functions;

public class BatchManagementFunction
{
    private readonly IBatchRepository _batchRepo;
    private readonly IRunbookRepository _runbookRepo;
    private readonly IMemberRepository _memberRepo;
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
        IMemberRepository memberRepo,
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
        _memberRepo = memberRepo;
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
    [Authorize(Policy = AuthConstants.AuthenticatedPolicy)]
    [Function("ListBatches")]
    public async Task<IActionResult> ListAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "batches")] HttpRequest req)
    {
        _logger.LogInformation("ListBatches request");

        // Parse query parameters
        int? runbookId = req.Query.TryGetValue("runbookId", out var rbId) && int.TryParse(rbId, out var rbIdVal)
            ? rbIdVal : null;
        string? status = req.Query.TryGetValue("status", out var st) ? st.ToString() : null;
        bool? isManual = req.Query.TryGetValue("manual", out var manual) && bool.TryParse(manual, out var manualVal)
            ? manualVal : null;
        int limit = req.Query.TryGetValue("limit", out var lim) && int.TryParse(lim, out var limVal) && limVal > 0 && limVal <= 100
            ? limVal : 100;
        int offset = req.Query.TryGetValue("offset", out var off) && int.TryParse(off, out var offVal) && offVal >= 0
            ? offVal : 0;

        var batches = await _batchRepo.ListAsync(runbookId, status, isManual, limit, offset);
        var batchList = batches.ToList();

        // Look up runbook names/versions and member counts
        var runbookCache = new Dictionary<int, (string Name, int Version)>();
        var enriched = new List<object>();
        foreach (var b in batchList)
        {
            if (!runbookCache.TryGetValue(b.RunbookId, out var rbInfo))
            {
                var rb = await _runbookRepo.GetByIdAsync(b.RunbookId);
                rbInfo = rb is not null ? (rb.Name, rb.Version) : ("unknown", 0);
                runbookCache[b.RunbookId] = rbInfo;
            }

            var members = await _memberRepo.GetActiveByBatchAsync(b.Id);
            enriched.Add(new
            {
                b.Id,
                RunbookName = rbInfo.Name,
                RunbookVersion = rbInfo.Version,
                b.BatchStartTime,
                b.Status,
                b.IsManual,
                b.CreatedBy,
                b.CurrentPhase,
                MemberCount = members.Count(),
                b.DetectedAt,
                b.InitDispatchedAt
            });
        }

        return new OkObjectResult(new
        {
            batches = enriched,
            limit,
            offset
        });
    }

    /// <summary>
    /// GET /api/batches/{id} - Get batch details
    /// </summary>
    [Authorize(Policy = AuthConstants.AuthenticatedPolicy)]
    [Function("GetBatch")]
    public async Task<IActionResult> GetAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "batches/{id:int}")] HttpRequest req,
        int id)
    {
        _logger.LogInformation("GetBatch request for {BatchId}", id);

        var batch = await _batchRepo.GetByIdAsync(id);
        if (batch is null)
            return new NotFoundObjectResult(new { error = $"Batch {id} not found" });

        var runbook = await _runbookRepo.GetByIdAsync(batch.RunbookId);
        var members = await _memberRepo.GetActiveByBatchAsync(id);
        var phases = await _phaseRepo.GetByBatchAsync(id);
        var initExecs = await _initRepo.GetByBatchAsync(id);

        // Parse runbook YAML to get available phase names
        var availablePhases = new List<string>();
        if (runbook is not null)
        {
            try
            {
                var definition = _parser.Parse(runbook.YamlContent);
                availablePhases = definition.Phases.Select(p => p.Name).ToList();
            }
            catch { /* best-effort */ }
        }

        return new OkObjectResult(new
        {
            batch = new
            {
                batch.Id,
                RunbookName = runbook?.Name ?? "unknown",
                RunbookVersion = runbook?.Version ?? 0,
                batch.BatchStartTime,
                batch.Status,
                batch.IsManual,
                batch.CreatedBy,
                batch.CurrentPhase,
                MemberCount = members.Count(),
                batch.DetectedAt,
                batch.InitDispatchedAt,
                AvailablePhases = availablePhases
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
    [Authorize(Policy = AuthConstants.AdminPolicy)]
    [Function("CreateBatch")]
    public async Task<IActionResult> CreateAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "batches")] HttpRequest req)
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

        const long MaxFileSizeBytes = 50 * 1024 * 1024; // 50MB
        if (file.Length > MaxFileSizeBytes)
            return new BadRequestObjectResult(new { error = "CSV file exceeds 50MB size limit" });

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
        var createdBy = req.GetUserIdentity();

        var result = await _manualBatch.CreateBatchAsync(runbook, definition, csvResult.Data!, createdBy);

        if (!result.Success)
        {
            return new BadRequestObjectResult(new { error = result.ErrorMessage });
        }

        return new OkObjectResult(new
        {
            success = true,
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
    [Authorize(Policy = AuthConstants.AuthenticatedPolicy)]
    [Function("ListBatchPhases")]
    public async Task<IActionResult> ListPhasesAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "batches/{id:int}/phases")] HttpRequest req,
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
    [Authorize(Policy = AuthConstants.AuthenticatedPolicy)]
    [Function("ListBatchSteps")]
    public async Task<IActionResult> ListStepsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "batches/{id:int}/steps")] HttpRequest req,
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
    [Authorize(Policy = AuthConstants.AdminPolicy)]
    [Function("AdvanceBatch")]
    public async Task<IActionResult> AdvanceAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "batches/{id:int}/advance")] HttpRequest req,
        int id)
    {
        _logger.LogInformation("AdvanceBatch request for {BatchId}", id);

        var batch = await _batchRepo.GetByIdAsync(id);
        if (batch is null)
            return new NotFoundObjectResult(new { error = $"Batch {id} not found" });

        if (!batch.IsManual)
            return new ConflictObjectResult(new { error = "Only manual batches can be advanced via API" });

        // Get runbook
        var runbook = await _runbookRepo.GetByIdAsync(batch.RunbookId);
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
    [Authorize(Policy = AuthConstants.AdminPolicy)]
    [Function("CancelBatch")]
    public async Task<IActionResult> CancelAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "batches/{id:int}/cancel")] HttpRequest req,
        int id)
    {
        _logger.LogInformation("CancelBatch request for {BatchId}", id);

        var batch = await _batchRepo.GetByIdAsync(id);
        if (batch is null)
            return new NotFoundObjectResult(new { error = $"Batch {id} not found" });

        if (batch.Status is BatchStatus.Completed or BatchStatus.Failed)
            return new ConflictObjectResult(new { error = $"Batch is already {batch.Status}" });

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
