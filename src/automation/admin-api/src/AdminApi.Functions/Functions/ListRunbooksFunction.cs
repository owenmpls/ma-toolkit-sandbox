using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AdminApi.Functions.Auth;
using AdminApi.Functions.Services;

namespace AdminApi.Functions.Functions;

public class ListRunbooksFunction
{
    private readonly IRunbookRepository _runbookRepo;
    private readonly ILogger<ListRunbooksFunction> _logger;

    public ListRunbooksFunction(
        IRunbookRepository runbookRepo,
        ILogger<ListRunbooksFunction> logger)
    {
        _runbookRepo = runbookRepo;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/runbooks - List all active runbooks
    /// </summary>
    [Authorize(Policy = AuthConstants.AuthenticatedPolicy)]
    [Function("ListRunbooks")]
    public async Task<IActionResult> ListActiveAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "runbooks")] HttpRequest req)
    {
        _logger.LogInformation("ListRunbooks request received");

        var runbooks = await _runbookRepo.GetActiveRunbooksAsync();

        return new OkObjectResult(new
        {
            runbooks = runbooks.Select(r => new
            {
                r.Id,
                r.Name,
                r.Version,
                r.DataTableName,
                r.OverdueBehavior,
                r.RerunInit,
                r.CreatedAt
            })
        });
    }

    /// <summary>
    /// GET /api/runbooks/{name}/versions - List all versions of a runbook
    /// </summary>
    [Authorize(Policy = AuthConstants.AuthenticatedPolicy)]
    [Function("ListRunbookVersions")]
    public async Task<IActionResult> ListVersionsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "runbooks/{name}/versions")] HttpRequest req,
        string name)
    {
        _logger.LogInformation("ListRunbookVersions request for {RunbookName}", name);

        var versions = await _runbookRepo.GetAllVersionsAsync(name);

        if (!versions.Any())
            return new NotFoundObjectResult(new { error = $"Runbook '{name}' not found" });

        return new OkObjectResult(new
        {
            name,
            versions = versions.Select(r => new
            {
                r.Id,
                r.Version,
                r.DataTableName,
                r.IsActive,
                r.OverdueBehavior,
                r.RerunInit,
                r.CreatedAt
            })
        });
    }
}
