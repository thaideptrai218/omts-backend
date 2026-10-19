using OMTS.Application.Common.Persistence;
using OMTS.Domain.Entities;
using OMTS.Domain.Enums;
using OMTS.Domain.Statuses;

namespace OMTS.Application.Authentication;

/// <summary>Log in (UC02), refresh the session and log out (UC07); session rules from NFR-01.</summary>
public sealed class AuthService(
    IAccountRepository accounts,
    ILoginSessionRepository sessions,
    IAccessGrantRepository grants,
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    ITokenService tokenService,
    TimeProvider timeProvider)
{
    public async Task<AuthResult> LoginAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var account = await accounts.FindByLoginIdentifierAsync(request.Identifier.Trim(), cancellationToken);
        if (account is null)
        {
            return AuthResult.Failure(AuthError.InvalidCredentials);
        }

        if (account.IsTemporarilyLocked(now))
        {
            return AuthResult.Failure(AuthError.TemporarilyLocked);
        }

        var passwordCheck = passwordHasher.Verify(account.PasswordHash, request.Password);
        if (passwordCheck == PasswordCheck.Failed)
        {
            account.RegisterFailedLogin(now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return AuthResult.Failure(account.IsTemporarilyLocked(now) ? AuthError.TemporarilyLocked : AuthError.InvalidCredentials);
        }

        // Status checks only after the password is proven, so they reveal nothing to a guesser.
        if (account.StatusId != StatusIds.Account.Active || !await HasEffectiveAccessAsync(account, now, cancellationToken))
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

        account.RegisterSuccessfulLogin(now);

        var lifetime = tokenService.GetSessionLifetime(request.ClientType);
        var refreshToken = tokenService.CreateRefreshToken();
        var session = new LoginSession
        {
            AccountId = account.Id,
            ClientType = request.ClientType,
            RefreshTokenHash = tokenService.HashRefreshToken(refreshToken),
            CreatedAt = now,
            RefreshTokenExpiresAt = now + lifetime.RefreshTokenLifetime,
            AbsoluteExpiresAt = now + lifetime.MaxLifetime,
            DeviceName = request.DeviceName,
            UserAgent = client.UserAgent,
            IpAddress = client.IpAddress,
        };
        sessions.Add(session);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AuthResult.Success(BuildTokens(account, session, refreshToken));
    }

    public async Task<AuthResult> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hash = tokenService.HashRefreshToken(request.RefreshToken);

        var session = await sessions.GetByRefreshTokenHashAsync(hash, cancellationToken);
        if (session is null)
        {
            // A token that was already rotated is being replayed: treat it as stolen and end the session.
            var replayed = await sessions.GetByPreviousRefreshTokenHashAsync(hash, cancellationToken);
            if (replayed is not null)
            {
                replayed.Revoke(now, "Refresh token reuse detected");
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return AuthResult.Failure(AuthError.SessionExpired);
        }

        if (!session.CanRefresh(now))
        {
            return AuthResult.Failure(AuthError.SessionExpired);
        }

        if (session.Account.StatusId != StatusIds.Account.Active || !await HasEffectiveAccessAsync(session.Account, now, cancellationToken))
        {
            session.Revoke(now, "Account no longer allowed to log in");
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return AuthResult.Failure(AuthError.AccessDenied);
        }

        var refreshToken = tokenService.CreateRefreshToken();
        session.Rotate(tokenService.HashRefreshToken(refreshToken), now, tokenService.GetSessionLifetime(session.ClientType).RefreshTokenLifetime);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AuthResult.Success(BuildTokens(session.Account, session, refreshToken));
    }

    /// <summary>Revokes the current session (UC07); returns false when it no longer exists.</summary>
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

    /// <summary>Staff can log in only while they hold at least one effective zone–role pair (UC02 – 2b).</summary>
    private Task<bool> HasEffectiveAccessAsync(Account account, DateTimeOffset now, CancellationToken cancellationToken) =>
        account.SubjectType != SubjectType.Staff
            ? Task.FromResult(true)
            : grants.HasEffectiveGrantAsync(account.Id, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken);

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
            // Traders and staff must verify a phone number before other functions (BR-34, UC02 – 3a).
            PhoneVerificationRequired: account.SubjectType is SubjectType.Trader or SubjectType.Staff && account.PhoneVerifiedAt is null);
    }
}
