using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using OMTS.Application.Common.Interfaces;

namespace OMTS.Application.Features.Authorization;

/// <summary>
/// Per-process cache of <see cref="AccountAccess"/>. Invalidation bumps a version that is part of the
/// key, so stale entries are simply never read again. With several API instances, each keeps its own
/// cache; the short lifetime bounds staleness until a distributed invalidation is added.
/// </summary>
public sealed class PermissionCache(IMemoryCache cache) : IPermissionCacheInvalidator
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, long> _accountVersions = new();
    private long _globalVersion;

    /// <summary>
    /// Take the key before loading from the database: if a change lands during the load, the result is
    /// stored under the old version and is never served.
    /// </summary>
    public string KeyFor(Guid accountId) =>
        $"omts:access:{accountId}:{Interlocked.Read(ref _globalVersion)}:{_accountVersions.GetValueOrDefault(accountId)}";

    public AccountAccess? Get(string key) => cache.TryGetValue(key, out AccountAccess? access) ? access : null;

    public void Set(string key, AccountAccess access) => cache.Set(key, access, Lifetime);

    public void InvalidateAccount(Guid accountId) => _accountVersions.AddOrUpdate(accountId, 1, (_, version) => version + 1);

    public void InvalidateAll() => Interlocked.Increment(ref _globalVersion);
}
