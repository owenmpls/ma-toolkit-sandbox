using Azure.Identity;
using Azure.Messaging.ServiceBus;
using MaToolkit.Automation.Shared.Services;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Scheduler.Functions.Services;
using Scheduler.Functions.Settings;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.Configure<SchedulerSettings>(
    builder.Configuration.GetSection(SchedulerSettings.SectionName));

builder.Services.AddSingleton<IDbConnectionFactory, SchedulerDbConnectionFactory>();

builder.Services.AddSingleton(sp =>
{
    var settings = sp.GetRequiredService<IOptions<SchedulerSettings>>().Value;
    return new ServiceBusClient(settings.ServiceBusNamespace, new DefaultAzureCredential());
});

builder.Services.AddHttpClient<IDatabricksQueryClient, DatabricksQueryClient>();

builder.Services.AddScoped<IRunbookRepository, RunbookRepository>();
builder.Services.AddScoped<IBatchRepository, BatchRepository>();
builder.Services.AddScoped<IMemberRepository, MemberRepository>();
builder.Services.AddScoped<IPhaseExecutionRepository, PhaseExecutionRepository>();
builder.Services.AddScoped<IStepExecutionRepository, StepExecutionRepository>();
builder.Services.AddScoped<IInitExecutionRepository, InitExecutionRepository>();
builder.Services.AddScoped<IDynamicTableManager, DynamicTableManager>();
builder.Services.AddScoped<IDataverseQueryClient, DataverseQueryClient>();
builder.Services.AddScoped<IDataSourceQueryService, DataSourceQueryService>();
builder.Services.AddScoped<IRunbookParser, RunbookParser>();
builder.Services.AddScoped<IMemberDiffService, MemberDiffService>();
builder.Services.AddScoped<IPhaseEvaluator, PhaseEvaluator>();
builder.Services.AddScoped<ITemplateResolver, TemplateResolver>();
builder.Services.AddScoped<IServiceBusPublisher, ServiceBusPublisher>();

// Scheduler orchestration services (refactored from SchedulerTimerFunction)
builder.Services.AddScoped<IMemberSynchronizer, MemberSynchronizer>();
builder.Services.AddScoped<IBatchDetector, BatchDetector>();
builder.Services.AddScoped<IPhaseDispatcher, PhaseDispatcher>();
builder.Services.AddScoped<IVersionTransitionHandler, VersionTransitionHandler>();
builder.Services.AddScoped<IPollingManager, PollingManager>();

builder.Services.AddApplicationInsightsTelemetryWorkerService();

builder.Build().Run();
