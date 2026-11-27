using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Models;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Infrastructure.Bootstrap;

/// <summary>
/// Until 27/11/2026 every seasonal worker account was made with one shared, well-known first password that never expired. Those
/// accounts that still hold it (never signed in to change it) get that password expired here, so it opens nothing; the Owner gives a
/// worker who needs a login a random one-time password with "reset password". Only an account linked to a seasonal worker whose hash
/// matches the retired password is touched, so the demo accounts and every changed password stay. Idempotent, and never fails the deploy.
/// </summary>
public sealed class RetiredSeasonalPasswordCleanup(OmtsDbContext db, IPasswordHasher passwordHasher, TimeProvider timeProvider, ILogger<RetiredSeasonalPasswordCleanup> logger)
{
    /// <summary>The retired shared password; kept only to recognise the accounts that still hold it.</summary>
    private const string RetiredPassword = "OMTS@1234";

    /// <summary>The "issued" date the retired password was given so that it never expired.</summary>
    private static readonly DateTimeOffset NeverExpiresIssuedAt = new(2999, 12, 31, 0, 0, 0, TimeSpan.Zero);

    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var seasonalAccountIds = db.Employees
                .Where(e => e.AccountId != null && e.TypePeriods.Any(p => p.Type == EmployeeType.SeasonalWorker))
                .Select(e => e.AccountId!.Value);
            var candidates = await db.Accounts
                .Where(a => a.MustChangePassword && a.TemporaryPasswordIssuedAt == NeverExpiresIssuedAt && seasonalAccountIds.Contains(a.Id))
                .ToListAsync(cancellationToken);
            var retired = candidates.Where(a => passwordHasher.Verify(a.PasswordHash, RetiredPassword) != PasswordCheck.Failed).ToList();
            if (retired.Count == 0)
            {
                return "Retired seasonal password: no account holds it.";
            }

            // An issue date older than the 24-hour lifetime makes the temporary password expired at the next login.
            var expired = timeProvider.GetUtcNow() - TimeSpan.FromDays(2);
            foreach (var account in retired)
            {
                account.TemporaryPasswordIssuedAt = expired;
            }

            await db.SaveChangesAsync(cancellationToken);
            return $"Retired seasonal password: expired on {retired.Count} account(s).";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The retired seasonal password could not be expired; the next deploy retries.");
            return "Retired seasonal password: skipped after an error (see the log).";
        }
    }
}
