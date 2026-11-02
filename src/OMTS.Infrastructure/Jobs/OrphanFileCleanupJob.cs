using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Features.Files;

namespace OMTS.Infrastructure.Jobs;

/// <summary>
/// Deletes raft photo and license files that no record points to (<see cref="OrphanFileCleanupService"/>). Each
/// category runs in its own scope, so a failing one is logged and skipped while the other continues.
/// </summary>
public sealed class OrphanFileCleanupJob(IServiceScopeFactory scopes, IOptions<OrphanFileCleanupOptions> options, ILogger<OrphanFileCleanupJob> logger)
{
    /// <summary>Number of files deleted; files written within the grace period before <paramref name="now"/> are kept.</summary>
    public async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        foreach (var category in OrphanFileCleanupService.Categories)
        {
            try
            {
                using var scope = scopes.CreateScope();
                deleted += await scope.ServiceProvider.GetRequiredService<OrphanFileCleanupService>()
                    .CleanAsync(category, now - options.Value.GracePeriod, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Orphan file clean-up of {Category} failed; skipped.", category);
            }
        }

        return deleted;
    }
}
