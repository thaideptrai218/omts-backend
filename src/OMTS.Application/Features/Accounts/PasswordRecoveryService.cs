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
/// ACS-03 – recover a forgotten password with a code sent to a verified phone number or email. The start
/// answer is always the same (MSG15), so it never reveals whether an account exists.
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
    /// Sends a code only to the exact identifier entered, only when it is verified on an Active account; a
    /// new start for the account (another delivery method) cancels its previous unused code.
    /// </summary>
    public async Task<RecoveryStarted> StartAsync(StartRecoveryRequest request, CancellationToken cancellationToken)
    {
        var phone = PhoneNumbers.Normalize(request.Identifier);
        var email = phone is null ? EmailAddresses.Normalize(request.Identifier) : null;
        var account = phone is not null
            ? await accounts.FindByPhoneAsync(phone, cancellationToken)
            : email is not null ? await accounts.FindByVerifiedEmailAsync(email, cancellationToken) : null;

        var verified = account is not null
            && account.StatusId == StatusIds.Account.Active
            && (phone is null || account.PhoneVerifiedAt is not null);

        // Unknown, unverified or locked identifiers and sender failures get the same answer and an id that
        // leads nowhere.
        var requestId = Guid.NewGuid();
        if (verified)
        {
            var channel = phone is not null ? VerificationChannel.Sms : VerificationChannel.Email;
            var started = await verification.StartAsync(VerificationPurpose.AccountRecovery, channel, phone ?? email!, account!.Id, cancellationToken);
            if (started.IsSuccess)
            {
                requestId = started.Value!.VerificationId;
            }
        }

        return new RecoveryStarted(requestId, UserMessages.RecoveryCodeMaybeSent.Text);
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
        if (account is null || account.StatusId != StatusIds.Account.Active)
        {
            return new AppError(ErrorKind.Validation, UserMessages.WrongVerificationCode);
        }

        var now = timeProvider.GetUtcNow();
        account.PasswordHash = passwordHasher.Hash(request.NewPassword);
        account.MustChangePassword = false;
        account.TemporaryPasswordIssuedAt = null;
        account.RegisterSuccessfulLogin(now);
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
}
