using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Identity;
using StackExchange.Redis;

namespace OMTS.Infrastructure.Identity.Revocation;

/// <summary>
/// Revoked sessions in Redis as <c>omts:auth:rev:s:{sessionId}</c> keys that expire once every access token issued
/// before the revocation has. A missing key never means "valid": it only means "not known to be revoked", and the
/// store says so (<see cref="SessionRevocationStatus.NotRevoked"/>) only while the <c>omts:auth:ready</c> marker shows
/// that the sweeper has copied every recent revocation. Every failure is <see cref="SessionRevocationStatus.Unknown"/>
/// and sends the request to the database.
/// </summary>
internal sealed class RedisSessionRevocationStore(
    IConnectionMultiplexer redis,
    RedisCircuitBreaker breaker,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider,
    ILogger<RedisSessionRevocationStore> logger) : ISessionRevocationStore
{
    // The marker is looked at once a second at most, so a request normally costs one Redis round trip, not two.
    private static readonly TimeSpan ReadyCacheDuration = TimeSpan.FromSeconds(1);

    private long _readyCachedUntil;

    /// <summary>How long a deny entry must live after the revocation.</summary>
    public TimeSpan DenyWindow => jwtOptions.Value.AccessTokenLifetime + SessionRevocationOptions.DenyMargin;

    public async ValueTask<SessionRevocationStatus> GetStatusAsync(Guid sessionId)
    {
        if (breaker.IsOpen)
        {
            return SessionRevocationStatus.Unknown;
        }

        try
        {
            var db = redis.GetDatabase();
            var key = RevocationKeys.Revoked(sessionId);
            var now = timeProvider.GetTimestamp();
            if (now < Volatile.Read(ref _readyCachedUntil))
            {
                var listed = await db.KeyExistsAsync(key);
                breaker.RecordSuccess();
                return listed ? SessionRevocationStatus.Revoked : SessionRevocationStatus.NotRevoked;
            }

            var batch = db.CreateBatch();
            var revoked = batch.KeyExistsAsync(key);
            var ready = batch.KeyExistsAsync(RevocationKeys.Ready);
            batch.Execute();
            await Task.WhenAll(revoked, ready);
            breaker.RecordSuccess();

            // A deny entry is trusted whether or not the store is complete.
            if (revoked.Result)
            {
                return SessionRevocationStatus.Revoked;
            }

            if (!ready.Result)
            {
                return SessionRevocationStatus.Unknown;
            }

            Volatile.Write(ref _readyCachedUntil, now + (long)(ReadyCacheDuration.TotalSeconds * timeProvider.TimestampFrequency));
            return SessionRevocationStatus.NotRevoked;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            breaker.RecordFailure();
            logger.LogWarning("Revocation store unavailable ({ExceptionType}); checking the database instead.", exception.GetType().Name);
            return SessionRevocationStatus.Unknown;
        }
    }

    public async Task<bool> TryMarkRevokedAsync(IReadOnlyCollection<RevokedSession> sessions)
    {
        if (sessions.Count == 0)
        {
            return true;
        }

        if (breaker.IsOpen)
        {
            return false;
        }

        try
        {
            var now = timeProvider.GetUtcNow();
            var window = DenyWindow;
            var batch = redis.GetDatabase().CreateBatch();
            var writes = new List<Task>(sessions.Count);
            foreach (var session in sessions)
            {
                // Revoked long enough ago that no token it could deny is left; a clock ahead of this one cannot stretch the entry.
                var remaining = session.RevokedAt + window - now;
                if (remaining <= TimeSpan.Zero)
                {
                    continue;
                }

                writes.Add(batch.StringSetAsync(RevocationKeys.Revoked(session.SessionId), "1", remaining < window ? remaining : window));
            }

            batch.Execute();
            await Task.WhenAll(writes);
            breaker.RecordSuccess();
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            breaker.RecordFailure();
            logger.LogWarning("Revocations could not be written to the store ({ExceptionType}); the sweeper will retry.", exception.GetType().Name);
            return false;
        }
    }

    /// <summary>Marks the store complete for another <see cref="SessionRevocationOptions.ReadyMarkerLifetime"/>.</summary>
    public async Task<bool> TryMarkReadyAsync()
    {
        if (breaker.IsOpen)
        {
            return false;
        }

        try
        {
            await redis.GetDatabase().StringSetAsync(RevocationKeys.Ready, "1", SessionRevocationOptions.ReadyMarkerLifetime);
            breaker.RecordSuccess();
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            breaker.RecordFailure();
            logger.LogWarning("Revocation store could not be marked complete ({ExceptionType}).", exception.GetType().Name);
            return false;
        }
    }

    /// <summary>Reachable and marked complete, i.e. requests are really answered from Redis.</summary>
    public async Task<bool> IsCompleteAsync()
    {
        if (breaker.IsOpen)
        {
            return false;
        }

        try
        {
            var complete = await redis.GetDatabase().KeyExistsAsync(RevocationKeys.Ready);
            breaker.RecordSuccess();
            return complete;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            breaker.RecordFailure();
            return false;
        }
    }
}
