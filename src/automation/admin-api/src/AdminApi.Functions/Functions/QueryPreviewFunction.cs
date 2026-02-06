using MaToolkit.Automation.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AdminApi.Functions.Services;

namespace AdminApi.Functions.Functions;

public class QueryPreviewFunction
{
    private readonly IRunbookRepository _runbookRepo;
    private readonly IRunbookParser _parser;
    private readonly IQueryPreviewService _previewService;
    private readonly ILogger<QueryPreviewFunction> _logger;

    public QueryPreviewFunction(
        IRunbookRepository runbookRepo,
        IRunbookParser parser,
        IQueryPreviewService previewService,
        ILogger<QueryPreviewFunction> logger)
    {
        _runbookRepo = runbookRepo;
        _parser = parser;
        _previewService = previewService;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/runbooks/{name}/query/preview - Execute query and return preview (no batch created)
    /// </summary>
    [Function("QueryPreview")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "runbooks/{name}/query/preview")] HttpRequest req,
        string name)
    {
        _logger.LogInformation("QueryPreview request for {RunbookName}", name);

        // Get runbook
        var runbook = await _runbookRepo.GetByNameAsync(name);
        if (runbook is null)
            return new NotFoundObjectResult(new { error = $"Runbook '{name}' not found" });

        // Parse YAML
        var definition = _parser.Parse(runbook.YamlContent);

        // Execute preview
        try
        {
            var response = await _previewService.ExecutePreviewAsync(definition);
            return new OkObjectResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Query preview failed for runbook {RunbookName}", name);
            return new BadRequestObjectResult(new { error = $"Query execution failed: {ex.Message}" });
        }
    }
}
