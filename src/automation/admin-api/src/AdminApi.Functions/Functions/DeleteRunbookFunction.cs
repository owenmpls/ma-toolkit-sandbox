using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AdminApi.Functions.Services;

namespace AdminApi.Functions.Functions;

public class DeleteRunbookFunction
{
    private readonly IRunbookRepository _runbookRepo;
    private readonly ILogger<DeleteRunbookFunction> _logger;

    public DeleteRunbookFunction(
        IRunbookRepository runbookRepo,
        ILogger<DeleteRunbookFunction> logger)
    {
        _runbookRepo = runbookRepo;
        _logger = logger;
    }

    /// <summary>
    /// DELETE /api/runbooks/{name}/versions/{version} - Deactivate (soft-delete) a specific runbook version
    /// </summary>
    [Function("DeleteRunbookVersion")]
    public async Task<IActionResult> DeleteVersionAsync(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "runbooks/{name}/versions/{version:int}")] HttpRequest req,
        string name,
        int version)
    {
        _logger.LogInformation("DeleteRunbookVersion request for {RunbookName} v{Version}", name, version);

        var deactivated = await _runbookRepo.DeactivateVersionAsync(name, version);

        if (!deactivated)
        {
            // Check if it exists but is already inactive
            var existing = await _runbookRepo.GetByNameAndVersionAsync(name, version);
            if (existing is null)
                return new NotFoundObjectResult(new { error = $"Runbook '{name}' version {version} not found" });

            return new ConflictObjectResult(new { error = $"Runbook '{name}' version {version} is already inactive" });
        }

        _logger.LogInformation("Deactivated runbook {RunbookName} v{Version}", name, version);

        return new OkObjectResult(new
        {
            message = $"Runbook '{name}' version {version} has been deactivated",
            name,
            version
        });
    }
}
