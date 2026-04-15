using IngestionOrchestrator.Functions.Services;
using IngestionOrchestrator.Functions.Settings;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddOptions<IngestionSettings>()
    .Bind(builder.Configuration.GetSection(IngestionSettings.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IConfigLoader>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<IngestionSettings>>().Value;
    var configPath = Path.IsPathRooted(settings.ConfigPath)
        ? settings.ConfigPath
        : Path.Combine(AppContext.BaseDirectory, settings.ConfigPath);
    return new ConfigLoader(configPath);
});

builder.Services.AddSingleton<IEntityResolver, EntityResolver>();
builder.Services.AddSingleton<ITenantResolver, TenantResolver>();
builder.Services.AddSingleton<IRunHistoryWriter, RunHistoryWriter>();
builder.Services.AddHttpClient<IContainerJobDispatcher, ContainerJobDispatcher>();
builder.Services.AddSingleton<IRunTracker, RunTracker>();
builder.Services.AddSingleton<IRunExecutor, RunExecutor>();

builder.Services.AddApplicationInsightsTelemetryWorkerService();

builder.Build().Run();
