using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Scheduler.Functions.Services;

public class BlobLeaseDistributedLock : IDistributedLock
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<BlobLeaseDistributedLock> _logger;
    private readonly Lazy<BlobServiceClient> _blobServiceClient;

    private const string ContainerName = "scheduler-locks";
    private const int LeaseDurationSeconds = 60;
    private static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(30);

    public BlobLeaseDistributedLock(IConfiguration configuration, ILogger<BlobLeaseDistributedLock> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _blobServiceClient = new Lazy<BlobServiceClient>(() =>
        {
            var accountName = _configuration["AzureWebJobsStorage:accountName"]
                ?? throw new InvalidOperationException("AzureWebJobsStorage:accountName configuration is required for distributed locking");
            return new BlobServiceClient(new Uri($"https://{accountName}.blob.core.windows.net"), new DefaultAzureCredential());
        });
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string lockName, CancellationToken ct = default)
    {
        var containerClient = _blobServiceClient.Value.GetBlobContainerClient(ContainerName);
        await containerClient.CreateIfNotExistsAsync(cancellationToken: ct);

        var blobClient = containerClient.GetBlobClient(lockName);

        // Ensure the blob exists
        if (!await blobClient.ExistsAsync(ct))
        {
            try
            {
                await blobClient.UploadAsync(new BinaryData(Array.Empty<byte>()), overwrite: false, cancellationToken: ct);
            }
            catch (Azure.RequestFailedException ex) when (ex.ErrorCode == "BlobAlreadyExists")
            {
                // Another instance created it — that's fine
            }
        }

        var leaseClient = blobClient.GetBlobLeaseClient();

        try
        {
            var lease = await leaseClient.AcquireAsync(TimeSpan.FromSeconds(LeaseDurationSeconds), cancellationToken: ct);
            _logger.LogInformation("Acquired distributed lock '{LockName}' with lease ID {LeaseId}", lockName, lease.Value.LeaseId);

            return new LeaseHandle(leaseClient, lockName, _logger);
        }
        catch (Azure.RequestFailedException ex) when (ex.ErrorCode == "LeaseAlreadyPresent")
        {
            _logger.LogDebug("Lock '{LockName}' is already held by another instance", lockName);
            return null;
        }
    }

    private sealed class LeaseHandle : IAsyncDisposable
    {
        private readonly BlobLeaseClient _leaseClient;
        private readonly string _lockName;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _renewCts = new();
        private readonly Task _renewTask;

        public LeaseHandle(BlobLeaseClient leaseClient, string lockName, ILogger logger)
        {
            _leaseClient = leaseClient;
            _lockName = lockName;
            _logger = logger;
            _renewTask = RenewLoopAsync(_renewCts.Token);
        }

        private async Task RenewLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(RenewInterval, ct);
                    await _leaseClient.RenewAsync(cancellationToken: ct);
                    _logger.LogDebug("Renewed lease for lock '{LockName}'", _lockName);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to renew lease for lock '{LockName}'", _lockName);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _renewCts.Cancel();

            try
            {
                await _renewTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            try
            {
                await _leaseClient.ReleaseAsync();
                _logger.LogInformation("Released distributed lock '{LockName}'", _lockName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release lock '{LockName}' — lease will expire automatically", _lockName);
            }

            _renewCts.Dispose();
        }
    }
}
