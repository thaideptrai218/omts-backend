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

        account.RegisterFailedLogin(now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return account.IsTemporarilyLocked(now)
            ? new AppError(ErrorKind.Locked, UserMessages.TooManyAttempts)
            : new AppError(ErrorKind.Validation, UserMessages.WrongCurrentPassword);
    }
}
