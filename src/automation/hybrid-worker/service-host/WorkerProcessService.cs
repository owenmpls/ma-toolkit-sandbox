using System.Diagnostics;

namespace MaToolkit.HybridWorker.ServiceHost;

public class WorkerProcessService(
    ILogger<WorkerProcessService> logger,
    IHostApplicationLifetime lifetime,
    IConfiguration configuration) : BackgroundService
{
    private readonly string _installPath = configuration.GetValue<string>("InstallPath")
        ?? @"C:\ProgramData\MaToolkit\HybridWorker";
    private readonly int _shutdownGraceSeconds = configuration.GetValue<int>("ShutdownGraceSeconds", 45);
    private Process? _workerProcess;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Hybrid Worker service host starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var workerScript = Path.Combine(_installPath, "current", "src", "worker.ps1");
            var logPath = Path.Combine(_installPath, "logs", "worker.log");

            if (!File.Exists(workerScript))
            {
                logger.LogError("Worker script not found: {Path}", workerScript);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                continue;
            }

            logger.LogInformation("Starting PowerShell worker: {Script}", workerScript);

            var logStream = new StreamWriter(logPath, append: true) { AutoFlush = true };

            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh.exe",
                Arguments = $"-NoProfile -NonInteractive -File \"{workerScript}\"",
                WorkingDirectory = Path.Combine(_installPath, "current"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Environment =
                {
                    ["HYBRID_WORKER_CONFIG_PATH"] = Path.Combine(
                        _installPath, "config", "worker-config.json"),
                    ["HYBRID_WORKER_INSTALL_PATH"] = _installPath
                }
            };

            _workerProcess = new Process { StartInfo = startInfo };
            _workerProcess.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    logStream.WriteLine($"{DateTime.UtcNow:O} [OUT] {e.Data}");
                }
            };
            _workerProcess.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    logStream.WriteLine($"{DateTime.UtcNow:O} [ERR] {e.Data}");
                    logger.LogWarning("Worker stderr: {Message}", e.Data);
                }
            };

            _workerProcess.Start();
            _workerProcess.BeginOutputReadLine();
            _workerProcess.BeginErrorReadLine();

            logger.LogInformation("Worker process started (PID: {PID}).", _workerProcess.Id);

            // Wait for process exit or cancellation
            try
            {
                await _workerProcess.WaitForExitAsync(stoppingToken);

                var exitCode = _workerProcess.ExitCode;
                logger.LogInformation("Worker process exited with code {ExitCode}.", exitCode);

                // Exit code 0 = clean shutdown (e.g., self-update staged). Restart.
                // Exit code 100 = update applied, restart immediately.
                // Other = unexpected, wait before restarting.
                if (exitCode != 0 && exitCode != 100)
                {
                    logger.LogWarning(
                        "Unexpected exit code {ExitCode}. Waiting 10s before restart.",
                        exitCode);
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Service is stopping — send graceful shutdown signal
                logger.LogInformation(
                    "Service stop requested. Sending Ctrl+C to worker (grace: {Seconds}s).",
                    _shutdownGraceSeconds);

                if (!_workerProcess.HasExited)
                {
                    // GenerateConsoleCtrlEvent requires the process to share our console.
                    // Since we redirect streams, we use taskkill with /T to signal the tree.
                    // The worker registers Console.CancelKeyPress to set WorkerRunning = false.
                    try
                    {
                        // Send Ctrl+Break to the process tree
                        using var killProc = Process.Start(new ProcessStartInfo
                        {
                            FileName = "taskkill",
                            Arguments = $"/PID {_workerProcess.Id} /T",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        });
                        killProc?.WaitForExit(5000);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to send graceful shutdown signal.");
                    }

                    // Wait for graceful shutdown
                    using var graceCts = new CancellationTokenSource(
                        TimeSpan.FromSeconds(_shutdownGraceSeconds));
                    try
                    {
                        await _workerProcess.WaitForExitAsync(graceCts.Token);
                        logger.LogInformation("Worker exited gracefully.");
                    }
                    catch (OperationCanceledException)
                    {
                        logger.LogWarning(
                            "Worker did not exit within grace period. Terminating.");
                        _workerProcess.Kill(entireProcessTree: true);
                    }
                }
            }
            finally
            {
                logStream.Dispose();
                _workerProcess?.Dispose();
                _workerProcess = null;
            }
        }

        logger.LogInformation("Hybrid Worker service host stopped.");
    }
}
