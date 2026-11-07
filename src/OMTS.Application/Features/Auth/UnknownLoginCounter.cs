using Microsoft.Extensions.Caching.Memory;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Auth;

/// <summary>
/// Failed-login counts of identifiers that match no account. They count and pause like the count of an account
/// (BR-ACCOUNT-05, see <see cref="AuthService"/>): the count does not age with time, the 5th failure pauses login for
/// 15 minutes, failures during the pause do not extend it, and the count starts again when it ends. The identifiers come
/// from anonymous callers, so the counts live in their own cache with a fixed number of entries: made-up identifiers
/// cannot grow server memory, and the shared cache (permissions) is never evicted by them. When the cache is full a new
/// identifier is simply not counted: nothing is evicted early (no compaction), so flooding cannot reset the count of an
/// identifier being guessed. Counting is serialised, so parallel failures of one identifier are all counted.
/// </summary>
public sealed class UnknownLoginCounter(int maxIdentifiers = UnknownLoginCounter.DefaultMaxIdentifiers, TimeProvider? timeProvider = null) : IDisposable
{
    public const int DefaultMaxIdentifiers = 100_000;

    // The count of an account never ages, but memory for made-up identifiers has to come back: a count is forgotten this
    // long after its last failure. A much longer time than the pause, so that spacing failures out does not tell them apart.
    private static readonly TimeSpan CountRetention = TimeSpan.FromHours(24);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = maxIdentifiers, CompactionPercentage = 0 });
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();

    /// <summary>Number of identifiers currently counted (never more than the limit).</summary>
    public int Count => _cache.Count;

    /// <summary>Counts one more failure for the identifier and returns its failures since its last pause ended.</summary>
    public int RegisterFailure(string identifier)
    {
        var key = Key(identifier);
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var counted = _cache.Get<Counted>(key);
            if (counted?.PausedUntil <= now)
            {
                counted = null;
            }

            var failures = (counted?.Failures ?? 0) + 1;
            var pausedUntil = counted?.PausedUntil;
            if (pausedUntil is null && failures >= Account.MaxFailedLogins)
            {
                pausedUntil = now + Account.TemporaryLockDuration;
            }

            _cache.Set(key, new Counted(failures, pausedUntil), new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow = CountRetention,
            });
            return failures;
        }
    }

    /// <summary>What is left of the pause of the identifier; null when it is not paused.</summary>
    public TimeSpan? PauseLeft(string identifier)
    {
        var key = Key(identifier);
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            return _cache.Get<Counted>(key)?.PausedUntil is { } until && until > now ? until - now : null;
        }
    }

    public void Dispose() => _cache.Dispose();

    private static string Key(string identifier) => identifier.ToUpperInvariant();

    private sealed record Counted(int Failures, DateTimeOffset? PausedUntil);
}
