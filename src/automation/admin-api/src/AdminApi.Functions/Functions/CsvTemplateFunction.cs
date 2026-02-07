using System.Text;
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

public class CsvTemplateFunction
{
    private readonly IRunbookRepository _runbookRepo;
    private readonly IRunbookParser _parser;
    private readonly ICsvTemplateService _templateService;
    private readonly ILogger<CsvTemplateFunction> _logger;

    public CsvTemplateFunction(
        IRunbookRepository runbookRepo,
        IRunbookParser parser,
        ICsvTemplateService templateService,
        ILogger<CsvTemplateFunction> logger)
    {
        _runbookRepo = runbookRepo;
        _parser = parser;
        _templateService = templateService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/runbooks/{name}/template - Download CSV template for manual batch creation
    /// </summary>
    [Authorize(Policy = AuthConstants.AuthenticatedPolicy)]
    [Function("CsvTemplate")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "runbooks/{name}/template")] HttpRequest req,
        string name)
    {
        _logger.LogInformation("CsvTemplate request for {RunbookName}", name);

        // Get runbook
        var runbook = await _runbookRepo.GetByNameAsync(name);
        if (runbook is null)
            return new NotFoundObjectResult(new { error = $"Runbook '{name}' not found" });

        // Parse YAML
        var definition = _parser.Parse(runbook.YamlContent);

        // Generate template
        var csvContent = _templateService.GenerateTemplate(definition);

        // Return as file download
        var bytes = Encoding.UTF8.GetBytes(csvContent);
        return new FileContentResult(bytes, "text/csv")
        {
            FileDownloadName = $"{name}_template.csv"
        };
    }
}
