namespace Query.Api.Resilience;

internal sealed class StrongFallbackGate(TimeProvider timeProvider)
{
    private const int FailureThreshold = 5;
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim bulkhead = new(initialCount: 32, maxCount: 32);
    private readonly Lock stateLock = new();
    private int consecutiveFailures;
    private DateTimeOffset openUntil;

    public IDisposable? TryAcquire()
    {
        lock (stateLock)
        {
            if (openUntil > timeProvider.GetUtcNow())
            {
                return null;
            }
        }

        return bulkhead.Wait(0) ? new Lease(bulkhead) : null;
    }

    public void RecordSuccess()
    {
        lock (stateLock)
        {
            consecutiveFailures = 0;
            openUntil = default;
        }
    }

    public void RecordFailure()
    {
        lock (stateLock)
        {
            consecutiveFailures++;
            if (consecutiveFailures >= FailureThreshold)
            {
                openUntil = timeProvider.GetUtcNow().Add(BreakDuration);
                consecutiveFailures = 0;
            }
        }
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
