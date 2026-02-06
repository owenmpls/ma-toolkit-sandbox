using MaToolkit.Automation.Shared.Services;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RunbookApi.Functions.Services;
using RunbookApi.Functions.Settings;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.Configure<RunbookApiSettings>(
    builder.Configuration.GetSection(RunbookApiSettings.SectionName));

builder.Services.AddSingleton<IDbConnectionFactory, RunbookApiDbConnectionFactory>();
builder.Services.AddScoped<IRunbookRepository, RunbookRepository>();
builder.Services.AddScoped<IRunbookParser, RunbookParser>();

builder.Services.AddApplicationInsightsTelemetryWorkerService();

builder.Build().Run();
