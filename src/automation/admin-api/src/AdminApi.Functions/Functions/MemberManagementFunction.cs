using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AdminApi.Functions.Auth;
using AdminApi.Functions.Services;
using AdminApi.Functions.Services.Repositories;

namespace AdminApi.Functions.Functions;

public class MemberManagementFunction
{
    private readonly IBatchRepository _batchRepo;
    private readonly IMemberRepository _memberRepo;
    private readonly IRunbookRepository _runbookRepo;
    private readonly IDynamicTableManager _dynamicTableManager;
    private readonly IRunbookParser _parser;
    private readonly ICsvUploadService _csvUpload;
    private readonly ILogger<MemberManagementFunction> _logger;

    public MemberManagementFunction(
        IBatchRepository batchRepo,
        IMemberRepository memberRepo,
        IRunbookRepository runbookRepo,
        IDynamicTableManager dynamicTableManager,
        IRunbookParser parser,
        ICsvUploadService csvUpload,
        ILogger<MemberManagementFunction> logger)
    {
        _batchRepo = batchRepo;
        _memberRepo = memberRepo;
        _runbookRepo = runbookRepo;
        _dynamicTableManager = dynamicTableManager;
        _parser = parser;
        _csvUpload = csvUpload;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/batches/{id}/members - List members for a batch
    /// </summary>
    [Authorize(Policy = AuthConstants.AuthenticatedPolicy)]
    [Function("ListBatchMembers")]
    public async Task<IActionResult> ListAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "batches/{id:int}/members")] HttpRequest req,
        int id)
    {
        _logger.LogInformation("ListBatchMembers request for {BatchId}", id);

        var batch = await _batchRepo.GetByIdAsync(id);
        if (batch is null)
            return new NotFoundObjectResult(new { error = $"Batch {id} not found" });

        var members = await _memberRepo.GetByBatchAsync(id);

        return new OkObjectResult(new
        {
            batchId = id,
            members = members.Select(m => new
            {
                m.Id,
                m.MemberKey,
                m.Status,
                m.AddedAt,
                m.RemovedAt,
                m.AddDispatchedAt,
                m.RemoveDispatchedAt
            })
        });
    }

    /// <summary>
    /// POST /api/batches/{id}/members - Add members from CSV upload
    /// </summary>
    [Authorize(Policy = AuthConstants.AdminPolicy)]
    [Function("AddBatchMembers")]
    public async Task<IActionResult> AddAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "batches/{id:int}/members")] HttpRequest req,
        int id)
    {
        _logger.LogInformation("AddBatchMembers request for {BatchId}", id);

        var batch = await _batchRepo.GetByIdAsync(id);
        if (batch is null)
            return new NotFoundObjectResult(new { error = $"Batch {id} not found" });

        if (!batch.IsManual)
            return new BadRequestObjectResult(new { error = "Members can only be added to manual batches via API" });

        if (batch.Status is BatchStatus.Completed or BatchStatus.Failed)
            return new BadRequestObjectResult(new { error = $"Cannot add members to a batch with status '{batch.Status}'" });

        // Get runbook
        var runbooks = await _runbookRepo.GetActiveRunbooksAsync();
        var runbook = runbooks.FirstOrDefault(r => r.Id == batch.RunbookId);
        if (runbook is null)
            return new BadRequestObjectResult(new { error = "Runbook not found or no longer active" });

        var definition = _parser.Parse(runbook.YamlContent);

        // Get CSV file
        if (!req.HasFormContentType)
            return new BadRequestObjectResult(new { error = "Request must be multipart/form-data" });

        var form = await req.ReadFormAsync();
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

        // Get existing member keys
        var existingMembers = await _memberRepo.GetByBatchAsync(id);
        var existingKeys = existingMembers
            .Where(m => m.Status == MemberStatus.Active)
            .Select(m => m.MemberKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Upsert data into dynamic table
        await _dynamicTableManager.UpsertDataAsync(
            runbook.DataTableName,
            definition.DataSource.PrimaryKey,
            definition.DataSource.BatchTimeColumn,
            csvResult.Data!,
            definition.DataSource.MultiValuedColumns);

        // Add new members
        var primaryKey = definition.DataSource.PrimaryKey;
        var addedCount = 0;
        var skippedCount = 0;

        foreach (System.Data.DataRow row in csvResult.Data!.Rows)
        {
            var memberKey = row[primaryKey]?.ToString() ?? string.Empty;

            if (existingKeys.Contains(memberKey))
            {
                skippedCount++;
                continue;
            }

            await _memberRepo.InsertAsync(new BatchMemberRecord
            {
                BatchId = id,
                MemberKey = memberKey
            });
            addedCount++;
        }

        _logger.LogInformation(
            "Added {AddedCount} members to batch {BatchId} (skipped {SkippedCount} duplicates)",
            addedCount, id, skippedCount);

        return new OkObjectResult(new
        {
            batchId = id,
            addedCount,
            skippedCount,
            warnings = csvResult.Warnings
        });
    }

    /// <summary>
    /// DELETE /api/batches/{batchId}/members/{memberId} - Remove a member from a batch
    /// </summary>
    [Authorize(Policy = AuthConstants.AdminPolicy)]
    [Function("RemoveBatchMember")]
    public async Task<IActionResult> RemoveAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "batches/{batchId:int}/members/{memberId:int}")] HttpRequest req,
        int batchId,
        int memberId)
    {
        _logger.LogInformation("RemoveBatchMember request for batch {BatchId}, member {MemberId}", batchId, memberId);

        var batch = await _batchRepo.GetByIdAsync(batchId);
        if (batch is null)
            return new NotFoundObjectResult(new { error = $"Batch {batchId} not found" });

        var member = await _memberRepo.GetByIdAsync(memberId);
        if (member is null)
            return new NotFoundObjectResult(new { error = $"Member {memberId} not found" });

        if (member.BatchId != batchId)
            return new BadRequestObjectResult(new { error = $"Member {memberId} does not belong to batch {batchId}" });

        if (member.Status == MemberStatus.Removed)
            return new BadRequestObjectResult(new { error = $"Member {memberId} is already removed" });

        if (!batch.IsManual)
            return new BadRequestObjectResult(new { error = "Members can only be removed from manual batches via API" });

        await _memberRepo.MarkRemovedAsync(memberId);

        _logger.LogInformation("Removed member {MemberId} from batch {BatchId}", memberId, batchId);

        return new OkObjectResult(new
        {
            message = $"Member {memberId} has been removed",
            batchId,
            memberId
        });
    }
}
