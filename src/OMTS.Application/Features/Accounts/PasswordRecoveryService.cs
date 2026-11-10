using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Accounts.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Verification;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts;

/// <summary>
/// ACS-03 – recover a forgotten password with a code sent by SMS to a verified phone number. Email is not a recovery
/// channel. The start answer is always the same (MSG-ACCOUNT-20), so it never reveals whether an account exists.
/// </summary>
public sealed class PasswordRecoveryService(
    IAccountRepository accounts,
    ILoginSessionRepository sessions,
    VerificationService verification,
    IPasswordHasher passwordHasher,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Sends a code only to the exact phone number entered, only when it is verified on an Active account; a
    /// new start for the account cancels its previous unused code.
    /// </summary>
    public async Task<RecoveryStarted> StartAsync(StartRecoveryRequest request, CancellationToken cancellationToken)
    {
        var phone = PhoneNumbers.Normalize(request.Identifier);
        var account = phone is not null ? await accounts.FindByPhoneAsync(phone, cancellationToken) : null;

        var verified = account is not null
            && account.StatusId == StatusIds.Account.Active
            && account.PhoneVerifiedAt is not null;

        // An email address, an unknown, unverified or locked number and sender failures get the same answer and an id
        // that leads nowhere.
        var requestId = Guid.NewGuid();
        if (verified)
        {
            var started = await verification.StartAsync(VerificationPurpose.AccountRecovery, VerificationChannel.Sms, phone!, account!.Id, cancellationToken);
            if (started.IsSuccess)
            {
                requestId = started.Value!.VerificationId;
            }
        }

        return new RecoveryStarted(requestId, UserMessages.RecoveryCodeMaybeSent.Text, UserMessages.RecoveryCodeMaybeSent.Code);
    }

    /// <summary>
    /// Sets the new password, clears the temporary-password state and ends every session. An invalid new
    /// password leaves the token usable.
    /// </summary>
    public async Task<Result<Done>> ResetAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        if (!PasswordPolicy.IsValid(request.NewPassword) || request.NewPassword != request.PasswordConfirmation)
        {
            return AppError.Invalid("Password");
        }

        var token = await verification.ConsumeTokenAsync(
            request.VerificationToken, [VerificationPurpose.AccountRecovery], null, null, cancellationToken, anyAccount: true);
        if (!token.IsSuccess)
        {
            return token.Error!;
        }

        var account = token.Value!.AccountId is { } id ? await accounts.GetByIdAsync(id, cancellationToken) : null;
        if (account is null || account.StatusId != StatusIds.Account.Active || !IsVerifiedContact(account, token.Value))
        {
            return new AppError(ErrorKind.Validation, UserMessages.WrongVerificationCode);
        }

        var now = timeProvider.GetUtcNow();
        account.PasswordHash = passwordHasher.Hash(request.NewPassword);
        account.MustChangePassword = false;
        account.TemporaryPasswordIssuedAt = null;
        // A recovered password also lifts a temporary login lock.
        account.FailedLoginCount = 0;
        account.LockoutEndAt = null;
        await sessions.RevokeAllAsync(account.Id, now, "Password reset", cancellationToken);
        audit.Record(new AuditEntry("Account.PasswordReset", nameof(Account))
        {
            RecordId = account.Id,
            RecordCode = account.Username,
            ActorAccountId = account.Id,
            After = new { Channel = token.Value.Channel.ToString(), SessionsRevoked = true },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Done.Value;
    }

    /// <summary>
    /// ACCOUNT-03 step 7: the token resets only the account that still has this verified phone number. A change of the number
    /// has already cancelled the token; this also holds for a recovery that started while the change was being saved.
    /// </summary>
    private static bool IsVerifiedContact(Account account, VerificationCode token) =>
        token.Channel == VerificationChannel.Sms && account.PhoneVerifiedAt is not null && account.PhoneNumber == token.Target;
}
