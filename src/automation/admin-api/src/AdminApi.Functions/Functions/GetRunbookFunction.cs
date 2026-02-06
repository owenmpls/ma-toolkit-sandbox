using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AdminApi.Functions.Services;

namespace AdminApi.Functions.Functions;

public class GetRunbookFunction
{
    private readonly IRunbookRepository _runbookRepo;
    private readonly ILogger<GetRunbookFunction> _logger;

    public GetRunbookFunction(
        IRunbookRepository runbookRepo,
        ILogger<GetRunbookFunction> logger)
    {
        _runbookRepo = runbookRepo;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/runbooks/{name} - Get the latest active version of a runbook
    /// </summary>
    [Function("GetRunbook")]
    public async Task<IActionResult> GetLatestAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "runbooks/{name}")] HttpRequest req,
        string name)
    {
        _logger.LogInformation("GetRunbook request for {RunbookName}", name);

        var runbook = await _runbookRepo.GetByNameAsync(name);
        if (runbook is null)
            return new NotFoundObjectResult(new { error = $"Runbook '{name}' not found" });

        return new OkObjectResult(new
        {
            runbook.Id,
            runbook.Name,
            runbook.Version,
            runbook.YamlContent,
            runbook.DataTableName,
            runbook.IsActive,
            runbook.OverdueBehavior,
            runbook.RerunInit,
            runbook.CreatedAt
        });
    }

    /// <summary>
    /// GET /api/runbooks/{name}/versions/{version} - Get a specific version of a runbook
    /// </summary>
    [Function("GetRunbookVersion")]
    public async Task<IActionResult> GetVersionAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "runbooks/{name}/versions/{version:int}")] HttpRequest req,
        string name,
        int version)
    {
        _logger.LogInformation("GetRunbookVersion request for {RunbookName} v{Version}", name, version);

        var runbook = await _runbookRepo.GetByNameAndVersionAsync(name, version);
        if (runbook is null)
            return new NotFoundObjectResult(new { error = $"Runbook '{name}' version {version} not found" });

        return new OkObjectResult(new
        {
            runbook.Id,
            runbook.Name,
            runbook.Version,
            runbook.YamlContent,
            runbook.DataTableName,
            runbook.IsActive,
            runbook.OverdueBehavior,
            runbook.RerunInit,
            runbook.CreatedAt
        });
    }
}
