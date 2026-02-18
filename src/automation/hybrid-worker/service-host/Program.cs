using MaToolkit.HybridWorker.ServiceHost;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "MaToolkitHybridWorker";
});
builder.Services.AddHostedService<WorkerProcessService>();

var host = builder.Build();
host.Run();
