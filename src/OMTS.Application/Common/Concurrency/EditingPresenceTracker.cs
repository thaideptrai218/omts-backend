namespace OMTS.Application.Common.Concurrency;

/// <summary>Someone with the edit form of a record open since <see cref="StartedAt"/>.</summary>
public sealed record RecordEditor(Guid AccountId, string Name, DateTimeOffset StartedAt);

/// <summary>
/// "Someone is editing" indicator without locking (NFR-13, MSG58). Kept in memory: an entry lives until it
/// is stopped (save or cancel) or until <see cref="Expiry"/> passes without a heartbeat (lost signal).
/// Saving still checks the version (NFR-09). Single-instance only; a scaled-out API would need a shared store.
/// </summary>
public sealed class EditingPresenceTracker(TimeProvider timeProvider)
{
    public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(5);

    private readonly Dictionary<(Guid RecordId, Guid AccountId), Entry> _entries = [];
    private readonly object _gate = new();

    /// <summary>Starts editing, or keeps an ongoing edit alive (its start time is kept).</summary>
    public void Heartbeat(Guid recordId, Guid accountId, string name)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            RemoveExpired(now);
            var key = (recordId, accountId);
            var startedAt = _entries.TryGetValue(key, out var existing) ? existing.StartedAt : now;
            _entries[key] = new Entry(name, startedAt, now);
        }
    }

    public void Stop(Guid recordId, Guid accountId)
    {
        lock (_gate)
        {
            _entries.Remove((recordId, accountId));
        }
    }

    /// <summary>Current editors of the record other than <paramref name="exceptAccountId"/>, earliest first.</summary>
    public IReadOnlyList<RecordEditor> EditorsOf(Guid recordId, Guid? exceptAccountId)
    {
        lock (_gate)
        {
            RemoveExpired(timeProvider.GetUtcNow());
            return _entries
                .Where(e => e.Key.RecordId == recordId && e.Key.AccountId != exceptAccountId)
                .Select(e => new RecordEditor(e.Key.AccountId, e.Value.Name, e.Value.StartedAt))
                .OrderBy(e => e.StartedAt)
                .ToList();
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var key in _entries.Where(e => now - e.Value.LastSeenAt >= Expiry).Select(e => e.Key).ToList())
        {
            _entries.Remove(key);
        }
    }

    private sealed record Entry(string Name, DateTimeOffset StartedAt, DateTimeOffset LastSeenAt);
}
