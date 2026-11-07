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

/// <summary>Result of ACS-04: fresh tokens for this device (every other session was ended) and MSG-ACCOUNT-11.</summary>
public sealed record PasswordChanged(AuthTokens Tokens, string Message, string MessageCode);

/// <summary>
/// ACCOUNT-04 – the logged-in user changes the password after proving identity (current password, or a code sent to
/// the verified email). The forced change after a temporary password asks for no proof: the user has just entered it
/// to log in (ACCOUNT-04.1).
/// </summary>
public sealed class PasswordChangeService(
    IAccountRepository accounts,
    ILoginSessionRepository sessions,
    VerificationService verification,
    AuthService authService,
    CurrentPasswordProof currentPassword,
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

    /// <param name="sessionId">The caller's session: its push devices move to the new session of this device.</param>
    public async Task<Result<PasswordChanged>> ChangeAsync(
        Guid accountId, Guid? sessionId, ChangePasswordRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            return new AppError(ErrorKind.Unauthorized, UserMessages.SessionExpired);
        }

        // BR-ACCOUNT-06: an expired temporary password can no longer be used, also by the session it opened.
        var now = timeProvider.GetUtcNow();
        if (account.IsTemporaryPasswordExpired(now))
        {
            return new AppError(ErrorKind.Forbidden, UserMessages.TemporaryPasswordExpired);
        }

        if (!PasswordPolicy.IsValid(request.NewPassword) || request.NewPassword != request.PasswordConfirmation)
        {
            return AppError.Invalid("Password");
        }

        // ACCOUNT-04 POST-1: the temporary password no longer works, so it cannot be the new one (ACCOUNT-04.0.E2).
        if (account.MustChangePassword && passwordHasher.Verify(account.PasswordHash, request.NewPassword) != PasswordCheck.Failed)
        {
            return AppError.Invalid("Password");
        }

        var proofKind = account.MustChangePassword ? "TemporaryPasswordLogin" : request.CurrentPassword is not null ? "CurrentPassword" : "EmailCode";
        if (!account.MustChangePassword && await CheckProofAsync(account, request, cancellationToken) is { } proof)
        {
            return proof;
        }

        account.PasswordHash = passwordHasher.Hash(request.NewPassword);
        account.MustChangePassword = false;
        account.TemporaryPasswordIssuedAt = null;
        await sessions.RevokeAllAsync(account.Id, now, "Password changed", cancellationToken);
        audit.Record(new AuditEntry("Account.PasswordChanged", nameof(Account))
        {
            RecordId = account.Id,
            RecordCode = account.Username,
            After = new { Proof = proofKind, SessionsRevoked = true },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var tokens = await authService.CreateSessionAsync(account, request.ClientType, request.DeviceName, client, cancellationToken, sessionId);
        return new PasswordChanged(tokens, UserMessages.PasswordChanged.Text, UserMessages.PasswordChanged.Code);
    }

    private async Task<AppError?> CheckProofAsync(Account account, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(request.CurrentPassword))
        {
            return await currentPassword.CheckAsync(account, request.CurrentPassword, cancellationToken);
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
