using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MaToolkit.Automation.Shared.Services;
using Orchestrator.Functions.Services;
using Orchestrator.Functions.Services.Handlers;
using Orchestrator.Functions.Settings;
using MaToolkit.Automation.Shared.Services.Repositories;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddOptions<OrchestratorSettings>()
    .Bind(builder.Configuration.GetSection(OrchestratorSettings.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IDbConnectionFactory, OrchestratorDbConnectionFactory>();

builder.Services.AddSingleton(sp =>
{
    var settings = sp.GetRequiredService<IOptions<OrchestratorSettings>>().Value;
    return new ServiceBusClient(settings.ServiceBusNamespace, new DefaultAzureCredential());
});

// Repositories
builder.Services.AddScoped<IRunbookRepository, RunbookRepository>();
builder.Services.AddScoped<IBatchRepository, BatchRepository>();
builder.Services.AddScoped<IMemberRepository, MemberRepository>();
builder.Services.AddScoped<IPhaseExecutionRepository, PhaseExecutionRepository>();
builder.Services.AddScoped<IStepExecutionRepository, StepExecutionRepository>();
builder.Services.AddScoped<IInitExecutionRepository, InitExecutionRepository>();

// Core services
builder.Services.AddScoped<IRunbookParser, RunbookParser>();
builder.Services.AddScoped<ITemplateResolver, TemplateResolver>();
builder.Services.AddScoped<IPhaseEvaluator, PhaseEvaluator>();
builder.Services.AddScoped<IDynamicTableReader, DynamicTableReader>();
builder.Services.AddScoped<IWorkerDispatcher, WorkerDispatcher>();
builder.Services.AddScoped<IRollbackExecutor, RollbackExecutor>();

// Progression service
builder.Services.AddScoped<IPhaseProgressionService, PhaseProgressionService>();

// Retry services
builder.Services.AddScoped<IRetryScheduler, RetryScheduler>();
builder.Services.AddScoped<IRetryCheckHandler, RetryCheckHandler>();

// Message handlers
builder.Services.AddScoped<IBatchInitHandler, BatchInitHandler>();
builder.Services.AddScoped<IPhaseDueHandler, PhaseDueHandler>();
builder.Services.AddScoped<IMemberAddedHandler, MemberAddedHandler>();
builder.Services.AddScoped<IMemberRemovedHandler, MemberRemovedHandler>();
builder.Services.AddScoped<IPollCheckHandler, PollCheckHandler>();
builder.Services.AddScoped<IResultProcessor, ResultProcessor>();

builder.Services.AddApplicationInsightsTelemetryWorkerService();

builder.Build().Run();
