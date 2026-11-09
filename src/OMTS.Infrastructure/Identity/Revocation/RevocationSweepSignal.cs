namespace OMTS.Infrastructure.Identity.Revocation;

/// <summary>Wakes the sweeper before its interval ends, e.g. after a revocation could not be written to Redis.</summary>
public sealed class RevocationSweepSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Request()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A sweep is already requested.
        }
    }

    /// <summary>Completes when a sweep is requested or the interval has passed.</summary>
    public Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken) => _signal.WaitAsync(interval, cancellationToken);
}
