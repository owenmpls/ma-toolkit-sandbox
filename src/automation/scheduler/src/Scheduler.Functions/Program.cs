using Azure.Identity;
using Azure.Messaging.ServiceBus;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Settings;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Scheduler.Functions.Services;
using Scheduler.Functions.Settings;
using MaToolkit.Automation.Shared.Services.Repositories;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.Configure<SchedulerSettings>(
    builder.Configuration.GetSection(SchedulerSettings.SectionName));

builder.Services.Configure<QueryClientSettings>(
    builder.Configuration.GetSection(QueryClientSettings.SectionName));

builder.Services.AddSingleton<IDbConnectionFactory, SchedulerDbConnectionFactory>();

builder.Services.AddSingleton(sp =>
{
    var settings = sp.GetRequiredService<IOptions<SchedulerSettings>>().Value;
    return new ServiceBusClient(settings.ServiceBusNamespace, new DefaultAzureCredential());
});

// Shared data source services
builder.Services.AddHttpClient<IDatabricksQueryClient, DatabricksQueryClient>();
builder.Services.AddScoped<IDataverseQueryClient, DataverseQueryClient>();
builder.Services.AddScoped<IDataSourceQueryService, DataSourceQueryService>();
builder.Services.AddScoped<IDynamicTableManager, DynamicTableManager>();

// Shared parsing and evaluation services
builder.Services.AddScoped<IRunbookParser, RunbookParser>();
builder.Services.AddScoped<IPhaseEvaluator, PhaseEvaluator>();

// Scheduler-specific repositories
builder.Services.AddScoped<IRunbookRepository, RunbookRepository>();
builder.Services.AddScoped<IAutomationSettingsRepository, AutomationSettingsRepository>();
builder.Services.AddScoped<IBatchRepository, BatchRepository>();
builder.Services.AddScoped<IMemberRepository, MemberRepository>();
builder.Services.AddScoped<IPhaseExecutionRepository, PhaseExecutionRepository>();
builder.Services.AddScoped<IStepExecutionRepository, StepExecutionRepository>();
builder.Services.AddScoped<IInitExecutionRepository, InitExecutionRepository>();

// Scheduler-specific services
builder.Services.AddScoped<IMemberDiffService, MemberDiffService>();
builder.Services.AddScoped<ITemplateResolver, TemplateResolver>();
builder.Services.AddScoped<IServiceBusPublisher, ServiceBusPublisher>();

// Scheduler orchestration services
builder.Services.AddScoped<IMemberSynchronizer, MemberSynchronizer>();
builder.Services.AddScoped<IBatchDetector, BatchDetector>();
builder.Services.AddScoped<IPhaseDispatcher, PhaseDispatcher>();
builder.Services.AddScoped<IVersionTransitionHandler, VersionTransitionHandler>();
builder.Services.AddScoped<IPollingManager, PollingManager>();

builder.Services.AddSingleton<IDistributedLock, BlobLeaseDistributedLock>();

builder.Services.AddApplicationInsightsTelemetryWorkerService();

builder.Build().Run();
