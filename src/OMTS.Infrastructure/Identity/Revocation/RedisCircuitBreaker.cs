namespace OMTS.Infrastructure.Identity.Revocation;

/// <summary>
/// Stops asking Redis for a while after it kept failing, so a dead Redis costs one short pause now and then instead
/// of a timeout on every request. Three failures within five seconds open it for ten seconds; the first call after
/// that is a trial that closes it on success and opens it again on failure.
/// </summary>
internal sealed class RedisCircuitBreaker(TimeProvider timeProvider)
{
    public const int FailureThreshold = 3;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan OpenFor = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _failures = new();
    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;
    private bool _trial;

    /// <summary>True while calls should be skipped.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                if (_openUntil == DateTimeOffset.MinValue)
                {
                    return false;
                }

                if (timeProvider.GetUtcNow() < _openUntil)
                {
                    return true;
                }

                _openUntil = DateTimeOffset.MinValue;
                _trial = true;
                return false;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _failures.Clear();
            _trial = false;
        }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            _failures.Enqueue(now);
            while (_failures.Count > 0 && now - _failures.Peek() > FailureWindow)
            {
                _failures.Dequeue();
            }

            if (_trial || _failures.Count >= FailureThreshold)
            {
                _openUntil = now + OpenFor;
                _failures.Clear();
                _trial = false;
            }
        }
    }
}
