using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Models;
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

    private const int MaxUserAgentLength = 500;
    private const int MaxDeviceNameLength = 200;
    private const int MaxIpAddressLength = 64;

    // Verified against the password when the identifier is unknown, so an unknown identifier and a wrong
    // password take the same time and cannot be told apart (MSG-ACCOUNT-05 gives no hint which field is wrong).
    private static string? _unknownAccountHash;

    public async Task<AuthResult> LoginAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        var identifier = request.Identifier.Trim();
        var account = await accounts.FindByLoginIdentifierAsync(
            identifier, PhoneNumbers.Normalize(identifier), EmailAddresses.Normalize(identifier), cancellationToken);
        if (account is null)
        {
            return FailUnknownIdentifier(identifier, request.Password);
        }

        // Hashed once: a retry re-applies the outcome to the re-read account, it does not hash again.
        PasswordCheck? passwordCheck = null;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (account.IsTemporarilyLocked(timeProvider.GetUtcNow()))
                {
                    // Hashed like a locked unknown identifier, so the answer time does not tell them apart.
                    if (passwordCheck is null)
                    {
                        VerifyAgainstDummyHash(request.Password);
                    }

                    return AuthResult.Failure(AuthError.TemporarilyLocked);
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
        var session = await sessions.GetByRefreshTokenHashAsync(tokenService.HashRefreshToken(request.RefreshToken), cancellationToken);
        if (session is null)
        {
            // Not the current token of any session: if it names a session, it is an old token being
            // replayed, so treat it as stolen and end that session.
            await RevokeReplayedSessionAsync(request.RefreshToken, now, cancellationToken);
            return AuthResult.Failure(AuthError.SessionExpired);
        }

        if (!session.CanRefresh(now))
        {
            return AuthResult.Failure(AuthError.SessionExpired);
        }

        var account = session.Account;
        var refusal = account.StatusId != StatusIds.Account.Active || !await HasEffectiveAccessAsync(account, cancellationToken)
            ? AuthError.AccessDenied
            : account.IsTemporaryPasswordExpired(now) ? AuthError.TemporaryPasswordExpired : (AuthError?)null;
        if (refusal is not null)
        {
            await RevokeAsync(session, now, "Account no longer allowed to log in", cancellationToken);
            return AuthResult.Failure(refusal.Value);
        }

        var refreshToken = tokenService.CreateRefreshToken(session.Id);
        session.Rotate(tokenService.HashRefreshToken(refreshToken), now, tokenService.GetSessionLifetime(session.ClientType).RefreshTokenLifetime);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // Another request rotated the same token first: two holders of one token means reuse.
            await unitOfWork.ReloadAsync(session, cancellationToken);
            session.Revoke(now, "Concurrent refresh with the same token");
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return AuthResult.Failure(AuthError.SessionExpired);
        }

        return AuthResult.Success(BuildTokens(account, session, refreshToken));
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
            return AuthResult.Failure(account.IsTemporarilyLocked(now) ? AuthError.TemporarilyLocked : AuthError.InvalidCredentials);
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

        return AuthResult.Success(await CreateSessionAsync(account, request.ClientType, request.DeviceName, client, cancellationToken));
    }

    /// <summary>
    /// Opens a new login session for an account whose identity is already proven (login, or right after a
    /// password change that revoked every other session) and saves it.
    /// </summary>
    /// <param name="replacesSessionId">
    /// The caller's session that a password change just revoked: the push devices it registered belong to this
    /// device and move to the new session, so the device keeps its notifications.
    /// </param>
    public async Task<AuthTokens> CreateSessionAsync(
        Account account, ClientType clientType, string? deviceName, ClientInfo client, CancellationToken cancellationToken,
        Guid? replacesSessionId = null)
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
        };
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

        return BuildTokens(account, session, refreshToken);
    }

    /// <summary>
    /// Unknown identifiers behave like accounts: same hashing time, and after 5 failures the same MSG-ACCOUNT-06
    /// for 15 minutes, so the lockout cannot be used to find out which identifiers exist.
    /// </summary>
    private AuthResult FailUnknownIdentifier(string identifier, string password)
    {
        VerifyAgainstDummyHash(password);

        var failures = unknownLogins.RegisterFailure(identifier);
        return AuthResult.Failure(failures >= Account.MaxFailedLogins ? AuthError.TemporarilyLocked : AuthError.InvalidCredentials);
    }

    private async Task RevokeReplayedSessionAsync(string refreshToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (tokenService.ReadSessionId(refreshToken) is not { } sessionId
            || await sessions.GetByIdAsync(sessionId, cancellationToken) is not { RevokedAt: null } replayed)
        {
            return;
        }

        await RevokeAsync(replayed, now, "Refresh token reuse detected", cancellationToken);
    }

    private async Task RevokeAsync(LoginSession session, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        session.Revoke(now, reason);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // The session changed meanwhile (rotated or revoked); revoke the current state.
            await unitOfWork.ReloadAsync(session, cancellationToken);
            session.Revoke(now, reason);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }

    private void VerifyAgainstDummyHash(string password) =>
        passwordHasher.Verify(_unknownAccountHash ??= passwordHasher.Hash(Guid.NewGuid().ToString()), password);

    /// <summary>Staff can log in only while they hold at least one effective zone–role pair (ACS-02).</summary>
    private Task<bool> HasEffectiveAccessAsync(Account account, CancellationToken cancellationToken) =>
        account.SubjectType != SubjectType.Staff
            ? Task.FromResult(true)
            : grants.HasEffectiveGrantAsync(account.Id, timeProvider.Today(), cancellationToken);

    private static string? Truncate(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length > maxLength ? value[..maxLength] : value;

    private AuthTokens BuildTokens(Account account, LoginSession session, string refreshToken)
    {
        var accessToken = tokenService.CreateAccessToken(account, session.Id);
        return new AuthTokens(
            accessToken.Token,
            accessToken.ExpiresAt,
            refreshToken,
            session.RefreshTokenExpiresAt,
            account.Id,
            account.DisplayName,
            account.SubjectType,
            account.MustChangePassword,
            // Traders and staff must verify a phone number before other functions (BR-34, ACS-02).
            PhoneVerificationRequired: account.SubjectType is SubjectType.Trader or SubjectType.Staff && account.PhoneVerifiedAt is null);
    }
}
