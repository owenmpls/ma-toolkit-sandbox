using Azure.Identity;
using Azure.Messaging.ServiceBus;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Settings;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using AdminApi.Functions.Auth;
using AdminApi.Functions.Services;
using AdminApi.Functions.Settings;
using MaToolkit.Automation.Shared.Services.Repositories;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Entra ID authentication
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthConstants.AdminPolicy, policy =>
        policy.RequireRole(AuthConstants.Roles.Admin));

    options.AddPolicy(AuthConstants.AuthenticatedPolicy, policy =>
        policy.RequireAuthenticatedUser());
});

builder.Services.AddOptions<AdminApiSettings>()
    .Bind(builder.Configuration.GetSection(AdminApiSettings.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<QueryClientSettings>()
    .Bind(builder.Configuration.GetSection(QueryClientSettings.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IDbConnectionFactory, AdminApiDbConnectionFactory>();

// Service Bus (optional - for dispatching phase events to orchestrator)
// When not configured, ManualBatchService will skip event publishing
builder.Services.AddSingleton(sp =>
{
    var settings = sp.GetRequiredService<IOptions<AdminApiSettings>>().Value;
    if (string.IsNullOrEmpty(settings.ServiceBusNamespace))
        return (ServiceBusClient?)null;
    return (ServiceBusClient?)new ServiceBusClient(settings.ServiceBusNamespace, new DefaultAzureCredential());
});

// Shared data source services
builder.Services.AddHttpClient<IDatabricksQueryClient, DatabricksQueryClient>();
builder.Services.AddScoped<IDataverseQueryClient, DataverseQueryClient>();
builder.Services.AddScoped<IDataSourceQueryService, DataSourceQueryService>();
builder.Services.AddScoped<IDynamicTableManager, DynamicTableManager>();

// Shared parsing and evaluation services
builder.Services.AddScoped<IPhaseEvaluator, PhaseEvaluator>();
builder.Services.AddScoped<IRunbookParser, RunbookParser>();

// Repositories
builder.Services.AddScoped<IRunbookRepository, RunbookRepository>();
builder.Services.AddScoped<IAutomationSettingsRepository, AutomationSettingsRepository>();
builder.Services.AddScoped<IBatchRepository, BatchRepository>();
builder.Services.AddScoped<IMemberRepository, MemberRepository>();
builder.Services.AddScoped<IPhaseExecutionRepository, PhaseExecutionRepository>();
builder.Services.AddScoped<IStepExecutionRepository, StepExecutionRepository>();
builder.Services.AddScoped<IInitExecutionRepository, InitExecutionRepository>();

// Admin services
builder.Services.AddScoped<IQueryPreviewService, QueryPreviewService>();
builder.Services.AddScoped<ICsvTemplateService, CsvTemplateService>();
builder.Services.AddScoped<ICsvUploadService, CsvUploadService>();
builder.Services.AddScoped<IManualBatchService, ManualBatchService>();

builder.Services.AddApplicationInsightsTelemetryWorkerService();

builder.Build().Run();
