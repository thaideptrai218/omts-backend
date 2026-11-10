using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Infrastructure.Identity.Revocation;

/// <summary>
/// Copies recent revocations from the database to Redis every few seconds, on every instance (writing a key twice
/// is harmless). It repairs what the inline write after a save missed (Redis down, timeout, an instance that crashed
/// between commit and write) and refills Redis after a flush or restart. Only after every key was written does it
/// mark the store complete; until then requests are checked against the database.
/// </summary>
internal sealed class SessionRevocationSweeper(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    IOptions<SessionRevocationOptions> options,
    RevocationSweepSignal signal,
    TimeProvider timeProvider,
    ILogger<SessionRevocationSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.UseRedis)
        {
            return;
        }

        // Resolved here, so connecting to Redis happens at startup and not inside the first request.
        var store = services.GetRequiredService<RedisSessionRevocationStore>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(store, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Revocation sweep failed ({ExceptionType}); retrying.", exception.GetType().Name);
            }

            try
            {
                await signal.WaitAsync(options.Value.SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SweepAsync(RedisSessionRevocationStore store, CancellationToken cancellationToken)
    {
        // Older revocations have no token left to deny.
        var cutoff = timeProvider.GetUtcNow() - store.DenyWindow;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var recent = await db.LoginSessions
            .AsNoTracking()
            .Where(s => s.RevokedAt >= cutoff)
            .Select(s => new { s.Id, s.RevokedAt })
            .ToListAsync(cancellationToken);

        var revoked = recent.Select(s => new RevokedSession(s.Id, s.RevokedAt!.Value)).ToList();
        if (await store.TryMarkRevokedAsync(revoked))
        {
            await store.TryMarkReadyAsync();
        }
    }
}
