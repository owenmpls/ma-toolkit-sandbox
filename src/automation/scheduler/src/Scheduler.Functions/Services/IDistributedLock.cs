namespace Scheduler.Functions.Services;

public interface IDistributedLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(string lockName, CancellationToken ct = default);
}
