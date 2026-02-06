using MaToolkit.Automation.Shared.Models.Db;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AdminApi.Functions.Models.Requests;
using AdminApi.Functions.Services;
using AdminApi.Functions.Services.Repositories;

namespace AdminApi.Functions.Functions;

public class AutomationSettingsFunction
{
    private readonly IAutomationSettingsRepository _automationRepo;
    private readonly IRunbookRepository _runbookRepo;
    private readonly ILogger<AutomationSettingsFunction> _logger;

    public AutomationSettingsFunction(
        IAutomationSettingsRepository automationRepo,
        IRunbookRepository runbookRepo,
        ILogger<AutomationSettingsFunction> logger)
    {
        _automationRepo = automationRepo;
        _runbookRepo = runbookRepo;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/runbooks/{name}/automation - Get automation status for a runbook
    /// </summary>
    [Function("GetAutomationSettings")]
    public async Task<IActionResult> GetAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "runbooks/{name}/automation")] HttpRequest req,
        string name)
    {
        _logger.LogInformation("GetAutomationSettings request for {RunbookName}", name);

        // Verify runbook exists
        var runbook = await _runbookRepo.GetByNameAsync(name);
        if (runbook is null)
            return new NotFoundObjectResult(new { error = $"Runbook '{name}' not found" });

        var settings = await _automationRepo.GetByNameAsync(name);

        return new OkObjectResult(new
        {
            runbookName = name,
            automationEnabled = settings?.AutomationEnabled ?? false,
            enabledAt = settings?.EnabledAt,
            enabledBy = settings?.EnabledBy,
            disabledAt = settings?.DisabledAt,
            disabledBy = settings?.DisabledBy
        });
    }

    /// <summary>
    /// PUT /api/runbooks/{name}/automation - Enable or disable automation for a runbook
    /// </summary>
    [Function("SetAutomationSettings")]
    public async Task<IActionResult> SetAsync(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "runbooks/{name}/automation")] HttpRequest req,
        string name)
    {
        _logger.LogInformation("SetAutomationSettings request for {RunbookName}", name);

        // Verify runbook exists
        var runbook = await _runbookRepo.GetByNameAsync(name);
        if (runbook is null)
            return new NotFoundObjectResult(new { error = $"Runbook '{name}' not found" });

        SetAutomationRequest? body;
        try
        {
            body = await req.ReadFromJsonAsync<SetAutomationRequest>();
        }
        catch (Exception ex)
        {
            return new BadRequestObjectResult(new { error = $"Invalid request body: {ex.Message}" });
        }

        if (body is null)
            return new BadRequestObjectResult(new { error = "Request body is required" });

        var existingSettings = await _automationRepo.GetByNameAsync(name);
        var now = DateTime.UtcNow;

        // TODO: Get user from auth context when EasyAuth is implemented
        var user = "system";

        var record = new AutomationSettingsRecord
        {
            RunbookName = name,
            AutomationEnabled = body.Enabled,
            EnabledAt = body.Enabled ? now : existingSettings?.EnabledAt,
            EnabledBy = body.Enabled ? user : existingSettings?.EnabledBy,
            DisabledAt = !body.Enabled ? now : existingSettings?.DisabledAt,
            DisabledBy = !body.Enabled ? user : existingSettings?.DisabledBy
        };

        await _automationRepo.UpsertAsync(record);

        _logger.LogInformation(
            "Automation {Action} for runbook {RunbookName}",
            body.Enabled ? "enabled" : "disabled",
            name);

        return new OkObjectResult(new
        {
            runbookName = name,
            automationEnabled = record.AutomationEnabled,
            enabledAt = record.EnabledAt,
            enabledBy = record.EnabledBy,
            disabledAt = record.DisabledAt,
            disabledBy = record.DisabledBy
        });
    }
}
