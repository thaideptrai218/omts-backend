using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Models;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Accounts;

/// <summary>
/// The logged-in user proves who they are with the current password (ACS-04 password change, ACS-05 contact
/// changes). Wrong entries count like failed logins, so a stolen session cannot guess the password: the 5th
/// failure blocks for 15 minutes (MSG69).
/// </summary>
public sealed class CurrentPasswordProof(IPasswordHasher passwordHasher, IUnitOfWork unitOfWork, TimeProvider timeProvider)
{
    /// <summary>Null when the password is right; a missing one is MSG01 "Current password".</summary>
    public async Task<AppError?> CheckAsync(Account account, string? password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(password))
        {
            return AppError.Invalid("Current password");
        }

        var now = timeProvider.GetUtcNow();
        if (account.IsTemporarilyLocked(now))
        {
            return new AppError(ErrorKind.Locked, UserMessages.TooManyAttempts);
        }

        if (passwordHasher.Verify(account.PasswordHash, password) != PasswordCheck.Failed)
        {
            return null;
        }

        // Parallel wrong entries conflict on the account row version: the loser re-reads the account and
        // counts its failure again, so a burst is counted in full and locks like failed logins do.
        for (var attempt = 1; ; attempt++)
        {
            account.RegisterFailedLogin(now);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (ConcurrencyConflictException) when (attempt < MaxSaveAttempts)
            {
                await unitOfWork.ReloadAsync(account, cancellationToken);
                if (account.IsTemporarilyLocked(now))
                {
                    break;
                }
            }
        }

        return account.IsTemporarilyLocked(now)
            ? new AppError(ErrorKind.Locked, UserMessages.TooManyAttempts)
            : new AppError(ErrorKind.Validation, UserMessages.WrongCurrentPassword);
    }

    // Each conflict round saves one entry; the per-IP limiter caps the burst well below this.
    private const int MaxSaveAttempts = 20;
}
