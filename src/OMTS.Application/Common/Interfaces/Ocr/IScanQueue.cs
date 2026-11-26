namespace OMTS.Application.Common.Interfaces.Ocr;

public enum ScanStatus
{
    Pending,
    Done,
    Failed,
}

/// <summary>What a scan job produced: the reading (any serialisable object), or the <see cref="OcrErrorCodes"/> code it failed with.</summary>
public sealed record ScanOutcome(object? Reading, string? ErrorCode)
{
    public static ScanOutcome Done(object reading) => new(reading, null);

    public static ScanOutcome Failed(string errorCode) => new(null, errorCode);
}

/// <summary>A scan as the poll sees it; <see cref="Reading"/> is set only when <see cref="Status"/> is Done.</summary>
public sealed record ScanSnapshot(ScanStatus Status, string? ErrorCode, object? Reading);

/// <summary>
/// Runs long photo readings (30 s to 2 min) in the background, so the HTTP request answers 202 at once and the client polls.
/// The one implementation (<c>InMemoryScanQueue</c>) runs inside the API process: a single API instance, and a restart loses
/// the scans in flight (the client scans again). Every asynchronous scan reuses it.
/// </summary>
public interface IScanQueue
{
    /// <summary>
    /// Queues <paramref name="work"/>, which runs on a worker with its own service scope (never the request's, which is gone by then).
    /// Returns the scan id, or null when the queue is full (the caller answers OCR_BUSY).
    /// </summary>
    /// <param name="ownerAccountId">The only account that may read the scan.</param>
    /// <param name="scope">What the scan is for, e.g. "boundary:{zoneId}": a poll through another route or zone does not find it.</param>
    Guid? TryEnqueue(Guid ownerAccountId, string scope, Func<IServiceProvider, CancellationToken, Task<ScanOutcome>> work);

    /// <summary>The scan, or null when it does not exist, expired (kept 15 minutes), belongs to another account or another scope.</summary>
    ScanSnapshot? Find(Guid scanId, Guid ownerAccountId, string scope);
}
