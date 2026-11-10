using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Models;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Auth.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Auth;

/// <summary>Log in, refresh the session and log out (ACS-02); session rules from NFR-01.</summary>
public sealed class AuthService(
    IAccountRepository accounts,
    ILoginSessionRepository sessions,
    IAccessGrantRepository grants,
    IPushDeviceRepository pushDevices,
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    ITokenService tokenService,
    UnknownLoginCounter unknownLogins,
    TimeProvider timeProvider)
{
    // Concurrent logins of one account (double click, web + mobile, a burst of guesses) conflict on its row
    // version; the loser re-reads the account and applies its outcome again. Each round saves one of them,
    // so this covers any burst the per-IP limiter lets through.
    private const int MaxSaveAttempts = 20;

    // A refresh whose save failed without another rotation having happened (a deadlock victim) is tried again.
    private const int MaxRefreshAttempts = 3;

    private const int MaxUserAgentLength = 500;
    private const int MaxDeviceNameLength = 200;
    private const int MaxIpAddressLength = 64;

    // Verified against the password when the identifier is unknown, so an unknown identifier and a wrong
    // password take the same time and cannot be told apart (MSG-ACCOUNT-05 gives no hint which field is wrong).
    private static string? _unknownAccountHash;

    public async Task<AuthResult> LoginAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        var identifier = request.Identifier.Trim();
        // An email address is not a login identifier: it finds nothing and fails like any unknown identifier.
        var phone = PhoneNumbers.Normalize(identifier);
        var account = await accounts.FindByLoginIdentifierAsync(identifier, phone, cancellationToken);
        if (account is null)
        {
            // Normalised as the lookup is: every format of one number finds one row, so it shares one count here too.
            return FailUnknownIdentifier(phone ?? identifier, request.Password);
        }

        // Hashed once: a retry re-applies the outcome to the re-read account, it does not hash again.
        PasswordCheck? passwordCheck = null;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var now = timeProvider.GetUtcNow();
                if (account.IsTemporarilyLocked(now))
                {
                    // Hashed like a locked unknown identifier, so the answer time does not tell them apart.
                    if (passwordCheck is null)
                    {
                        VerifyAgainstDummyHash(request.Password);
                    }

                    return AuthResult.Paused(account.LockoutEndAt!.Value - now);
                }

                passwordCheck ??= passwordHasher.Verify(account.PasswordHash, request.Password);
                return await LoginAsync(account, request, passwordCheck.Value, client, cancellationToken);
            }
            catch (ConcurrencyConflictException) when (attempt < MaxSaveAttempts)
            {
                await unitOfWork.ReloadAsync(account, cancellationToken);
            }
        }
    }

    public async Task<AuthResult> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var tokenHash = tokenService.HashRefreshToken(request.RefreshToken);
        var session = await sessions.GetByRefreshTokenHashAsync(tokenHash, cancellationToken);
        if (session is null)
        {
            // Not the current token of any session. The token the last rotation replaced is still answered for its
            // grace period; any other old token is being replayed, so treat it as stolen and end that session.
            if (await sessions.GetByPreviousRefreshTokenHashAsync(tokenHash, cancellationToken) is { } superseded
                && superseded.IsWithinRefreshGrace(tokenHash, now))
            {
                return await RefreshWithinGraceAsync(superseded, request.RefreshToken, now, cancellationToken);
            }

            var replayReason = await RejectReplayedTokenAsync(request.RefreshToken, now, cancellationToken);
            return AuthResult.RefreshRefused(AuthError.SessionExpired, replayReason);
        }

        for (var attempt = 1; ; attempt++)
        {
            if (!session.CanRefresh(now))
            {
                return AuthResult.RefreshRefused(AuthError.SessionExpired, ReasonOfEnded(session));
            }

            if (await RefusalToRefreshAsync(session, now, cancellationToken) is { } refusal)
            {
                return refusal;
            }

            var lifetime = tokenService.GetSessionLifetime(session.ClientType);
            var refreshToken = tokenService.CreateRefreshToken(session.Id);
            session.Rotate(tokenService.HashRefreshToken(refreshToken), now, lifetime.RefreshTokenLifetime, lifetime.RefreshGrace);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (ConcurrencyConflictException)
            {
                // Another request rotated the same token first (or the database picked this save as a deadlock victim).
                await unitOfWork.ReloadAsync(session, cancellationToken);
                if (session.RefreshTokenHash == tokenHash)
                {
                    // Nothing rotated the session, so the token is not being replayed: try again, and when the
                    // attempts run out report the failure instead of ending a healthy session.
                    if (attempt < MaxRefreshAttempts)
                    {
                        continue;
                    }

                    throw;
                }

                // The winner's rotation made this request's token the replaced one: answer it as the grace case. Only
                // a token that is no longer the replaced one (a genuine replay) ends the session.
                if (!session.IsWithinRefreshGrace(tokenHash, now))
                {
                    await RevokeAsync(session, now, "Refresh token reuse detected", cancellationToken, SessionRevokeCause.TokenReuse);
                    return AuthResult.RefreshRefused(AuthError.SessionExpired, RefreshFailureReason.Reuse);
                }

                return await RefreshWithinGraceAsync(session, request.RefreshToken, now, cancellationToken);
            }

            return AuthResult.Success(BuildTokens(session.Account, session, refreshToken));
        }
    }

    /// <summary>Revokes the current session; returns false when it no longer exists.</summary>
    public async Task<bool> LogoutAsync(Guid accountId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(sessionId, accountId, cancellationToken);
        if (session is null)
        {
            return false;
        }

        session.Revoke(timeProvider.GetUtcNow(), "Logout");
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Checked on every authenticated request so revoked sessions cannot be reused (NFR-01).</summary>
    public Task<bool> IsSessionActiveAsync(Guid accountId, Guid sessionId, CancellationToken cancellationToken) =>
        sessions.IsActiveAsync(accountId, sessionId, timeProvider.GetUtcNow(), cancellationToken);

    private async Task<AuthResult> LoginAsync(
        Account account, LoginRequest request, PasswordCheck passwordCheck, ClientInfo client, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (passwordCheck == PasswordCheck.Failed)
        {
            account.RegisterFailedLogin(now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return account.IsTemporarilyLocked(now)
                ? AuthResult.Paused(account.LockoutEndAt!.Value - now)
                : AuthResult.Failure(AuthError.InvalidCredentials);
        }

        // Status checks only after the password is proven, so they reveal nothing to a guesser.
        if (account.StatusId != StatusIds.Account.Active || !await HasEffectiveAccessAsync(account, cancellationToken))
        {
            return AuthResult.Failure(AuthError.AccessDenied);
        }

        if (account.IsTemporaryPasswordExpired(now))
        {
            return AuthResult.Failure(AuthError.TemporaryPasswordExpired);
        }

        if (passwordCheck == PasswordCheck.SuccessRehashNeeded)
        {
            account.PasswordHash = passwordHasher.Hash(request.Password);
        }

        // Saved before the session is added: only this save can conflict, and a retry must not leave an
        // already added session behind.
        account.RegisterSuccessfulLogin(now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var created = await CreateSessionAsync(
            account, request.ClientType, request.DeviceName, client, AuthMethod.Password, request.DeviceId, cancellationToken);
        return created.IsSuccess ? AuthResult.Success(created.Value!) : AuthResult.Failure(AuthError.AccessDenied);
    }

    /// <summary>
    /// Opens a new login session for an account whose identity is already proven (login, or right after a
    /// password change that revoked every other session) and saves it.
    /// </summary>
    /// <param name="authMethod">How the person proved their identity; ignored when <paramref name="replacesSessionId"/> names a session.</param>
    /// <param name="deviceId">The client's random device id, hashed before it is stored; null, empty or <c>unknown</c> means no device.</param>
    /// <param name="replacesSessionId">
    /// The caller's session that a password change just revoked: the push devices it registered belong to this
    /// device and move to the new session, so the device keeps its notifications. The new session also keeps that
    /// session's device and sign-in method, so the device does not drop out of the device list.
    /// </param>
    /// <returns>
    /// The tokens, or Forbidden when the account stopped being Active between the caller's checks and this save (a lock
    /// racing the login): the session is then revoked in a further save, so no usable session is left behind.
    /// </returns>
    public async Task<Result<AuthTokens>> CreateSessionAsync(
        Account account, ClientType clientType, string? deviceName, ClientInfo client, AuthMethod authMethod, string? deviceId,
        CancellationToken cancellationToken, Guid? replacesSessionId = null)
    {
        var now = timeProvider.GetUtcNow();
        var lifetime = tokenService.GetSessionLifetime(clientType);
        var session = new LoginSession
        {
            AccountId = account.Id,
            ClientType = clientType,
            CreatedAt = now,
            RefreshTokenExpiresAt = now + lifetime.RefreshTokenLifetime,
            AbsoluteExpiresAt = now + lifetime.MaxLifetime,
            DeviceName = Truncate(deviceName, MaxDeviceNameLength),
            UserAgent = Truncate(client.UserAgent, MaxUserAgentLength),
            IpAddress = Truncate(client.IpAddress, MaxIpAddressLength),
            AuthMethod = authMethod,
            DeviceKey = DeviceKeyOf(deviceId),
        };
        if (replacesSessionId is { } previousId && await sessions.GetAsync(previousId, account.Id, cancellationToken) is { } previous)
        {
            session.AuthMethod = previous.AuthMethod;
            session.DeviceKey = previous.DeviceKey;
        }

        var refreshToken = tokenService.CreateRefreshToken(session.Id);
        session.RefreshTokenHash = tokenService.HashRefreshToken(refreshToken);
        sessions.Add(session);
        if (replacesSessionId is { } replaced)
        {
            foreach (var device in await pushDevices.ListForSessionAsync(replaced, cancellationToken))
            {
                device.LoginSessionId = session.Id;
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // The caller checked the status before this save. A lock that committed meanwhile revoked only the sessions it
        // could see, so look again now that this session is stored.
        if (!await sessions.IsActiveAsync(account.Id, session.Id, now, cancellationToken))
        {
            await RevokeAsync(session, now, "Account no longer allowed to log in", cancellationToken);
            return new AppError(ErrorKind.Forbidden, UserMessages.AccountUnusable);
        }

        return BuildTokens(account, session, refreshToken);
    }

    /// <summary>The stored key of a client's device id, or null when the client sent none (see <see cref="DeviceIds"/>).</summary>
    public string? DeviceKeyOf(string? deviceId) =>
        DeviceIds.Normalize(deviceId) is { } normalized ? tokenService.DeriveDeviceKey(normalized) : null;

    /// <summary>
    /// Unknown identifiers behave like accounts: same hashing time, and after 5 failures the same MSG-ACCOUNT-06
    /// with the time left of a 15-minute pause, so the pause cannot be used to find out which identifiers exist.
    /// </summary>
    private AuthResult FailUnknownIdentifier(string identifier, string password)
    {
        VerifyAgainstDummyHash(password);

        unknownLogins.RegisterFailure(identifier);
        return unknownLogins.PauseLeft(identifier) is { } pauseLeft
            ? AuthResult.Paused(pauseLeft)
            : AuthResult.Failure(AuthError.InvalidCredentials);
    }

    /// <summary>
    /// The replaced refresh token inside its grace period: a new access token, but no new refresh token and no
    /// second rotation. The presented token is echoed back with the session's current expiry, so a client that
    /// stores whatever it receives keeps working; <see cref="AuthTokens.RefreshTokenRotated"/> tells it nothing changed.
    /// The session, account and grants are checked exactly as for a rotation.
    /// </summary>
    private async Task<AuthResult> RefreshWithinGraceAsync(
        LoginSession session, string presentedRefreshToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!session.CanRefresh(now))
        {
            return AuthResult.RefreshRefused(AuthError.SessionExpired, ReasonOfEnded(session));
        }

        if (await RefusalToRefreshAsync(session, now, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        return AuthResult.Success(BuildTokens(session.Account, session, presentedRefreshToken, refreshTokenRotated: false));
    }

    /// <summary>A refresh is refused for an account that may no longer log in; that also ends the session.</summary>
    private async Task<AuthResult?> RefusalToRefreshAsync(LoginSession session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var account = session.Account;
        var refusal = account.StatusId != StatusIds.Account.Active || !await HasEffectiveAccessAsync(account, cancellationToken)
            ? AuthError.AccessDenied
            : account.IsTemporaryPasswordExpired(now) ? AuthError.TemporaryPasswordExpired : (AuthError?)null;
        if (refusal is null)
        {
            return null;
        }

        await RevokeAsync(session, now, "Account no longer allowed to log in", cancellationToken);
        return AuthResult.RefreshRefused(refusal.Value, RefreshFailureReason.Revoked);
    }

    /// <summary>
    /// A token that is neither current nor within its grace period. A session that is still usable is ended as possibly stolen
    /// (reuse); one that already ended answers with how it ended. A token that names no session (made up, or the session was
    /// cleaned up long ago) is expired.
    /// </summary>
    private async Task<RefreshFailureReason> RejectReplayedTokenAsync(string refreshToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (tokenService.ReadSessionId(refreshToken) is not { } sessionId
            || await sessions.GetByIdAsync(sessionId, cancellationToken) is not { } replayed)
        {
            return RefreshFailureReason.Expired;
        }

        if (replayed.RevokedAt is not null || !replayed.IsUsable(now))
        {
            return ReasonOfEnded(replayed);
        }

        await RevokeAsync(replayed, now, "Refresh token reuse detected", cancellationToken, SessionRevokeCause.TokenReuse);
        return RefreshFailureReason.Reuse;
    }

    /// <summary>How a session that cannot be refreshed ended: by a person or the system, by reuse, or by running out of time.</summary>
    private static RefreshFailureReason ReasonOfEnded(LoginSession session) => session.RevokedAt is null
        ? RefreshFailureReason.Expired
        : session.RevokeCause == SessionRevokeCause.TokenReuse ? RefreshFailureReason.Reuse : RefreshFailureReason.Revoked;

    private async Task RevokeAsync(
        LoginSession session, DateTimeOffset now, string reason, CancellationToken cancellationToken,
        SessionRevokeCause cause = SessionRevokeCause.Revoked)
    {
        session.Revoke(now, reason, cause);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // The session changed meanwhile (rotated or revoked); revoke the current state.
            await unitOfWork.ReloadAsync(session, cancellationToken);
            session.Revoke(now, reason, cause);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }

    private void VerifyAgainstDummyHash(string password) =>
        passwordHasher.Verify(_unknownAccountHash ??= passwordHasher.Hash(Guid.NewGuid().ToString()), password);

    /// <summary>Staff can log in only while they hold at least one Effective zone–role pair (ACCOUNT-02 step 4, AC-01).</summary>
    internal Task<bool> HasEffectiveAccessAsync(Account account, CancellationToken cancellationToken) =>
        account.SubjectType != SubjectType.Staff
            ? Task.FromResult(true)
            : grants.HasEffectiveGrantAsync(account.Id, cancellationToken);

    private static string? Truncate(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length > maxLength ? value[..maxLength] : value;

    private AuthTokens BuildTokens(Account account, LoginSession session, string refreshToken, bool refreshTokenRotated = true)
    {
        var accessToken = tokenService.CreateAccessToken(account, session.Id, session.AbsoluteExpiresAt);
        return new AuthTokens(
            accessToken.Token,
            accessToken.ExpiresAt,
            refreshToken,
            session.RefreshTokenExpiresAt,
            account.Id,
            account.DisplayName,
            account.SubjectType,
            account.MustChangePassword,
            // Every account must verify a phone number before other functions (BR-ACCOUNT-03, ACCOUNT-02.1).
            PhoneVerificationRequired: account.PhoneVerifiedAt is null,
            RefreshTokenRotated: refreshTokenRotated);
    }
}
