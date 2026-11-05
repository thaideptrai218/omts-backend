namespace OMTS.Application.Features.Verification;

/// <summary>
/// One start at a time for one purpose and contact, within this host. Parallel starts that had all saved their request saw
/// each other, all refused and removed themselves, so nobody got a code and each was told to wait. With the gate the first one
/// sends and the others find its code. Several hosts are still safe: the checks on the saved rows hold, and starts on different
/// hosts may refuse each other, which costs a retry and never an extra code.
/// </summary>
public sealed class VerificationStartGate
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Waits for the turn of this key; disposing the result gives it to the next start.</summary>
    public async Task<IDisposable> EnterAsync(string key, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Holders++;
        }

        try
        {
            await entry.Turn.WaitAsync(cancellationToken);
        }
        catch
        {
            Release(key, entry, holdsTurn: false);
            throw;
        }

        return new Lease(this, key, entry);
    }

    private void Release(string key, Entry entry, bool holdsTurn)
    {
        if (holdsTurn)
        {
            entry.Turn.Release();
        }

        lock (_entries)
        {
            if (--entry.Holders == 0)
            {
                _entries.Remove(key);
                entry.Turn.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Turn { get; } = new(1, 1);

        public int Holders { get; set; }
    }

    private sealed class Lease(VerificationStartGate gate, string key, Entry entry) : IDisposable
    {
        public void Dispose() => gate.Release(key, entry, holdsTurn: true);
    }
}
