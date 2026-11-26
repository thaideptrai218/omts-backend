using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Infrastructure.Ocr;

namespace OMTS.Infrastructure.Jobs;

/// <summary>
/// The queue of the asynchronous photo scans, inside the API process: a small bounded <see cref="Channel{T}"/>
/// (<c>Ocr:Scans:QueueCapacity</c> waiting, <c>MaxConcurrent</c> running) read by that many workers.
/// <list type="bullet">
/// <item>Each job holds its photo (up to 10 MB, plus the base64 copies of the call), so the queue is short, and one account has at most
/// one scan pending or running: a second start is refused like a full queue (OCR_BUSY).</item>
/// <item>The client polls for 120 s only. A job that waited longer than <c>MaxQueueWaitSeconds</c> when a worker takes it ends Failed/TIMEOUT
/// without calling Gemini.</item>
/// <item>Scans and results live in <see cref="IMemoryCache"/>, keyed by scan id. A result is kept <c>ResultMinutes</c> (15) counted from
/// its completion; a Pending scan never expires while it can still run. <c>Find</c> decides by the clock, not by eviction timing.</item>
/// <item>One API instance only (the staging compose): another instance, or a restart, does not know a scan, and the client scans again.</item>
/// <item>A job runs in its own DI scope, is stopped after <c>JobTimeoutSeconds</c>, and whatever it throws only fails that scan.</item>
/// </list>
/// </summary>
public sealed class InMemoryScanQueue : BackgroundService, IScanQueue
{
    private readonly Channel<Job> _jobs;
    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<InMemoryScanQueue> _logger;
    private readonly ScanQueueOptions _options;
    private readonly ConcurrentDictionary<Guid, byte> _accountsWithAScan = new();

    public InMemoryScanQueue(
        IMemoryCache cache, IServiceScopeFactory scopes, IOptions<OcrProviderOptions> options, TimeProvider clock, ILogger<InMemoryScanQueue> logger)
    {
        (_cache, _scopes, _clock, _logger, _options) = (cache, scopes, clock, logger, options.Value.Scans);
        // Wait mode: TryWrite returns false when full. The Drop modes would return true and lose the job.
        _jobs = Channel.CreateBounded<Job>(new BoundedChannelOptions(Math.Max(1, _options.QueueCapacity)) { FullMode = BoundedChannelFullMode.Wait });
    }

    public static string CacheKey(Guid scanId) => $"ocr-scan:{scanId:N}";

    public Guid? TryEnqueue(Guid ownerAccountId, string scope, Func<IServiceProvider, CancellationToken, Task<ScanOutcome>> work)
    {
        if (!_accountsWithAScan.TryAdd(ownerAccountId, 0))
        {
            _logger.LogWarning("An account started a photo scan while another of its scans is pending; refused");
            return null;
        }

        var id = Guid.NewGuid();
        var now = _clock.GetUtcNow();
        // Stored before the job can run, so a fast worker never finds nothing to complete. A Pending scan outlives its longest wait and run.
        var pendingFor = TimeSpan.FromSeconds(_options.MaxQueueWaitSeconds + _options.JobTimeoutSeconds) + ResultTime();
        Store(id, new Entry(ownerAccountId, scope, ScanStatus.Pending, null, null, now + pendingFor));
        if (_jobs.Writer.TryWrite(new Job(id, ownerAccountId, scope, work, now)))
        {
            return id;
        }

        _cache.Remove(CacheKey(id));
        _accountsWithAScan.TryRemove(ownerAccountId, out _);
        _logger.LogWarning("Photo scan queue is full ({Capacity} waiting); the scan is refused", _options.QueueCapacity);
        return null;
    }

    public ScanSnapshot? Find(Guid scanId, Guid ownerAccountId, string scope) =>
        _cache.TryGetValue(CacheKey(scanId), out Entry? entry) && entry is not null
        && entry.Owner == ownerAccountId && entry.Scope == scope && entry.ExpiresAt > _clock.GetUtcNow()
            ? new ScanSnapshot(entry.Status, entry.ErrorCode, entry.Reading)
            : null;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Math.Max(1, _options.MaxConcurrent)).Select(_ => Task.Run(() => WorkAsync(stoppingToken), stoppingToken)));

    private async Task WorkAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _jobs.Reader.ReadAllAsync(stoppingToken))
            {
                await RunAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: the scans still queued are lost, as documented.
        }
    }

    private async Task RunAsync(Job job, CancellationToken stoppingToken)
    {
        var outcome = ScanOutcome.Failed(OcrErrorCodes.Unavailable);
        try
        {
            if (_clock.GetUtcNow() - job.EnqueuedAt > TimeSpan.FromSeconds(_options.MaxQueueWaitSeconds))
            {
                _logger.LogWarning("A photo scan waited more than {Seconds} s in the queue; the client has given up, so Gemini is not called", _options.MaxQueueWaitSeconds);
                outcome = ScanOutcome.Failed(OcrErrorCodes.Timeout);
            }
            else
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                limit.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.JobTimeoutSeconds)));
                await using var scope = _scopes.CreateAsyncScope();
                outcome = await job.Work(scope.ServiceProvider, limit.Token);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("A photo scan was stopped after {Seconds} s", _options.JobTimeoutSeconds);
            outcome = ScanOutcome.Failed(OcrErrorCodes.Timeout);
        }
        catch (Exception exception)
        {
            // Only the type: the message of a reader may hold text read from a photo.
            _logger.LogError("A photo scan failed unexpectedly: {Error}", exception.GetType().Name);
        }

        var status = outcome.ErrorCode is null && outcome.Reading is not null ? ScanStatus.Done : ScanStatus.Failed;
        // The result's time counts from now, its completion; the job (and its photo) is released with this frame.
        Store(job.Id, new Entry(job.Owner, job.Scope, status, status == ScanStatus.Done ? null : outcome.ErrorCode ?? OcrErrorCodes.BadResponse,
            status == ScanStatus.Done ? outcome.Reading : null, _clock.GetUtcNow() + ResultTime()));
        _accountsWithAScan.TryRemove(job.Owner, out _);
    }

    private TimeSpan ResultTime() => TimeSpan.FromMinutes(Math.Max(1, _options.ResultMinutes));

    private void Store(Guid id, Entry entry) =>
        // The cache entry outlives the logical expiry a little, so Find decides by the clock, not by eviction timing.
        _cache.Set(CacheKey(id), entry, entry.ExpiresAt.AddMinutes(1));

    private sealed record Entry(Guid Owner, string Scope, ScanStatus Status, string? ErrorCode, object? Reading, DateTimeOffset ExpiresAt);

    private sealed record Job(Guid Id, Guid Owner, string Scope, Func<IServiceProvider, CancellationToken, Task<ScanOutcome>> Work, DateTimeOffset EnqueuedAt);
}
