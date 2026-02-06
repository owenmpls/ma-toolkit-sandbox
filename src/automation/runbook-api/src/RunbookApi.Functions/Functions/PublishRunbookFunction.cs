using System.Text.RegularExpressions;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using RunbookApi.Functions.Models.Requests;
using RunbookApi.Functions.Services;

namespace RunbookApi.Functions.Functions;

public class PublishRunbookFunction
{
    private readonly IRunbookRepository _runbookRepo;
    private readonly IRunbookParser _parser;
    private readonly ILogger<PublishRunbookFunction> _logger;

    public PublishRunbookFunction(
        IRunbookRepository runbookRepo,
        IRunbookParser parser,
        ILogger<PublishRunbookFunction> logger)
    {
        _runbookRepo = runbookRepo;
        _parser = parser;
        _logger = logger;
    }

    [Function("PublishRunbook")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "runbooks")] HttpRequest req)
    {
        _logger.LogInformation("PublishRunbook request received");

        PublishRunbookRequest? body;
        try
        {
            body = await req.ReadFromJsonAsync<PublishRunbookRequest>();
        }
        catch (Exception ex)
        {
            return new BadRequestObjectResult(new { error = $"Invalid request body: {ex.Message}" });
        }

        if (body is null)
            return new BadRequestObjectResult(new { error = "Request body is required" });

        if (string.IsNullOrWhiteSpace(body.Name))
            return new BadRequestObjectResult(new { error = "name is required" });

        if (string.IsNullOrWhiteSpace(body.YamlContent))
            return new BadRequestObjectResult(new { error = "yaml_content is required" });

        // Validate overdue_behavior
        var overdueBehavior = body.OverdueBehavior?.ToLowerInvariant() ?? "rerun";
        if (overdueBehavior is not ("rerun" or "ignore"))
            return new BadRequestObjectResult(new { error = "overdue_behavior must be 'rerun' or 'ignore'" });

        // Parse and validate YAML
        RunbookDefinition definition;
        try
        {
            definition = _parser.Parse(body.YamlContent);
        }
        catch (Exception ex)
        {
            return new BadRequestObjectResult(new { error = $"YAML parse error: {ex.Message}" });
        }

        var validationErrors = _parser.Validate(definition);
        if (validationErrors.Count > 0)
            return new BadRequestObjectResult(new { errors = validationErrors });

        // Verify name matches YAML content
        if (!string.Equals(definition.Name, body.Name, StringComparison.OrdinalIgnoreCase))
        {
            return new BadRequestObjectResult(new
            {
                error = $"Request name '{body.Name}' does not match YAML name '{definition.Name}'"
            });
        }

        // Determine next version
        var maxVersion = await _runbookRepo.GetMaxVersionAsync(body.Name);
        var newVersion = maxVersion + 1;

        // Generate data table name
        var sanitizedName = SanitizeName(body.Name);
        var dataTableName = $"runbook_{sanitizedName}_v{newVersion}";

        // Deactivate previous versions
        if (newVersion > 1)
        {
            await _runbookRepo.DeactivatePreviousVersionsAsync(body.Name, newVersion);
            _logger.LogInformation("Deactivated previous versions of runbook {RunbookName}", body.Name);
        }

        // Insert new runbook record
        var runbookId = await _runbookRepo.InsertAsync(new RunbookRecord
        {
            Name = body.Name,
            Version = newVersion,
            YamlContent = body.YamlContent,
            DataTableName = dataTableName,
            OverdueBehavior = overdueBehavior,
            RerunInit = body.RerunInit
        });

        _logger.LogInformation(
            "Published runbook {RunbookName} v{Version} (id: {RunbookId}, table: {DataTableName})",
            body.Name, newVersion, runbookId, dataTableName);

        return new OkObjectResult(new
        {
            runbookId,
            version = newVersion,
            dataTableName
        });
    }

    private static string SanitizeName(string name)
    {
        return Regex.Replace(name, @"[^a-zA-Z0-9]", "_").ToLowerInvariant();
    }
}
