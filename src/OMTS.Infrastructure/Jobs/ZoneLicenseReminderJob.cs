using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Infrastructure.Jobs;

/// <summary>
/// FARM-03 expiring license reminder. Each license runs in its own scope and unit of work, so one failing
/// license is logged and skipped while the others continue.
/// </summary>
public sealed class ZoneLicenseReminderJob(IServiceScopeFactory scopes, ILogger<ZoneLicenseReminderJob> logger)
{
    /// <summary>Number of notices sent; running twice on the same day sends nothing new.</summary>
    public async Task<int> RunOnceAsync(DateOnly today, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Guid> candidates;
        using (var scope = scopes.CreateScope())
        {
            candidates = await scope.ServiceProvider.GetRequiredService<ZoneLicenseReminderService>().CandidateIdsAsync(today, cancellationToken);
        }

        var sent = 0;
        foreach (var documentId in candidates)
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<ZoneLicenseReminderService>().RemindAsync(documentId, today, cancellationToken))
                {
                    sent++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Zone license reminder failed for license {LicenseId}; skipped.", documentId);
            }
        }

        return sent;
    }
}
