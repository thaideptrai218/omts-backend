using Microsoft.Extensions.Caching.Memory;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Auth;

/// <summary>
/// Failed-login counts of identifiers that match no account (they are locked out like accounts, see
/// <see cref="AuthService"/>). The identifiers come from anonymous callers, so the counts live in their own
/// cache with a fixed number of entries: made-up identifiers cannot grow server memory, and the shared cache
/// (permissions) is never evicted by them. When the cache is full a new identifier is simply not counted.
/// </summary>
public sealed class UnknownLoginCounter(int maxIdentifiers = UnknownLoginCounter.DefaultMaxIdentifiers) : IDisposable
{
    public const int DefaultMaxIdentifiers = 100_000;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = maxIdentifiers });

    /// <summary>Number of identifiers currently counted (never more than the limit).</summary>
    public int Count => _cache.Count;

    /// <summary>Counts one more failure for the identifier and returns its failures in the lock window.</summary>
    public int RegisterFailure(string identifier)
    {
        var key = identifier.ToUpperInvariant();
        var failures = _cache.Get<int>(key) + 1;
        _cache.Set(key, failures, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = Account.TemporaryLockDuration,
        });
        return failures;
    }

    public void Dispose() => _cache.Dispose();
}
