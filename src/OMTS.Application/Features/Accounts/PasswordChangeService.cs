using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Models;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Accounts.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Auth;
using OMTS.Application.Features.Auth.Dtos;
using OMTS.Application.Features.Verification;
using OMTS.Application.Features.Verification.Dtos;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts;

/// <summary>Result of ACS-04: fresh tokens for this device (every other session was ended) and MSG18.</summary>
public sealed record PasswordChanged(AuthTokens Tokens, string Message);

/// <summary>
/// ACS-04 – the logged-in user changes the password after proving identity (current password, or a code
/// sent to the verified email), including the forced change after a temporary password.
/// </summary>
public sealed class PasswordChangeService(
    IAccountRepository accounts,
    ILoginSessionRepository sessions,
    VerificationService verification,
    AuthService authService,
    IPasswordHasher passwordHasher,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>Sends a ChangePassword code to the verified email; without one, explains the alternatives.</summary>
    public async Task<Result<VerificationStarted>> SendEmailCodeAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            return AppError.NotFound;
        }

        if (account.Email is null || account.EmailVerifiedAt is null)
        {
            return new AppError(ErrorKind.Validation, UserMessages.NoVerifiedEmail);
        }

        return await verification.StartAsync(VerificationPurpose.ChangePassword, VerificationChannel.Email, account.Email, account.Id, cancellationToken);
    }

    public async Task<Result<PasswordChanged>> ChangeAsync(
        Guid accountId, ChangePasswordRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            return new AppError(ErrorKind.Unauthorized, UserMessages.SessionExpired);
        }

        if (!PasswordPolicy.IsValid(request.NewPassword) || request.NewPassword != request.PasswordConfirmation)
        {
            return AppError.Invalid("Password");
        }

        var proof = await CheckProofAsync(account, request, cancellationToken);
        if (proof is not null)
        {
            return proof;
        }

        var now = timeProvider.GetUtcNow();
        account.PasswordHash = passwordHasher.Hash(request.NewPassword);
        account.MustChangePassword = false;
        account.TemporaryPasswordIssuedAt = null;
        await sessions.RevokeAllAsync(account.Id, now, "Password changed", cancellationToken);
        audit.Record(new AuditEntry("Account.PasswordChanged", nameof(Account))
        {
            RecordId = account.Id,
            RecordCode = account.Username,
            After = new { Proof = request.CurrentPassword is not null ? "CurrentPassword" : "EmailCode", SessionsRevoked = true },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var tokens = await authService.CreateSessionAsync(account, request.ClientType, request.DeviceName, client, cancellationToken);
        return new PasswordChanged(tokens, UserMessages.PasswordChanged.Text);
    }

    private async Task<AppError?> CheckProofAsync(Account account, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(request.CurrentPassword))
        {
            return passwordHasher.Verify(account.PasswordHash, request.CurrentPassword) == PasswordCheck.Failed
                ? new AppError(ErrorKind.Validation, UserMessages.WrongCurrentPassword)
                : null;
        }

        if (string.IsNullOrEmpty(request.VerificationToken))
        {
            return AppError.Invalid("Current password");
        }

        var token = await verification.ConsumeTokenAsync(
            request.VerificationToken, [VerificationPurpose.ChangePassword], account.Id, account.Email, cancellationToken);
        return token.IsSuccess ? null : token.Error;
    }
}
