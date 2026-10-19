using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces;
using OMTS.Domain.Entities;
using OMTS.Domain.Enums;
using OMTS.Domain.Statuses;

namespace OMTS.Application.Authentication;

/// <summary>Log in (UC02), refresh the session and log out (UC07); session rules from NFR-01.</summary>
public sealed class AuthService(
    IOmtsDbContext db,
    IPasswordHasher passwordHasher,
    ITokenService tokenService,
    TimeProvider timeProvider)
{
    public async Task<AuthResult> LoginAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        var identifier = request.Identifier.Trim();
        var now = timeProvider.GetUtcNow();

        // Username, verified phone number or verified email (UC02 step 1).
        var account = await db.Accounts.FirstOrDefaultAsync(
            a => a.Username == identifier
                || (a.PhoneNumber == identifier && a.PhoneVerifiedAt != null)
                || (a.Email == identifier && a.EmailVerifiedAt != null),
            cancellationToken);

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
            await db.SaveChangesAsync(cancellationToken);
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
        db.LoginSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        return AuthResult.Success(BuildTokens(account, session, refreshToken));
    }

    public async Task<AuthResult> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hash = tokenService.HashRefreshToken(request.RefreshToken);

        var session = await db.LoginSessions
            .Include(s => s.Account)
            .FirstOrDefaultAsync(s => s.RefreshTokenHash == hash, cancellationToken);

        if (session is null)
        {
            // A token that was already rotated is being replayed: treat it as stolen and end the session.
            var replayed = await db.LoginSessions.FirstOrDefaultAsync(s => s.PreviousRefreshTokenHash == hash, cancellationToken);
            if (replayed is not null)
            {
                replayed.Revoke(now, "Refresh token reuse detected");
                await db.SaveChangesAsync(cancellationToken);
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
            await db.SaveChangesAsync(cancellationToken);
            return AuthResult.Failure(AuthError.AccessDenied);
        }

        var refreshToken = tokenService.CreateRefreshToken();
        session.Rotate(tokenService.HashRefreshToken(refreshToken), now, tokenService.GetSessionLifetime(session.ClientType).RefreshTokenLifetime);
        await db.SaveChangesAsync(cancellationToken);

        return AuthResult.Success(BuildTokens(session.Account, session, refreshToken));
    }

    /// <summary>Revokes the current session (UC07); returns false when it no longer exists.</summary>
    public async Task<bool> LogoutAsync(Guid accountId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.LoginSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.AccountId == accountId, cancellationToken);
        if (session is null)
        {
            return false;
        }

        session.Revoke(timeProvider.GetUtcNow(), "Logout");
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Checked on every authenticated request so revoked sessions cannot be reused (NFR-01).</summary>
    public Task<bool> IsSessionActiveAsync(Guid accountId, Guid sessionId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return db.LoginSessions.AnyAsync(
            s => s.Id == sessionId
                && s.AccountId == accountId
                && s.RevokedAt == null
                && s.AbsoluteExpiresAt > now
                && s.Account.StatusId == StatusIds.Account.Active,
            cancellationToken);
    }

    /// <summary>Staff can log in only while they hold at least one effective zone–role pair (UC02 – 2b).</summary>
    private async Task<bool> HasEffectiveAccessAsync(Account account, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (account.SubjectType != SubjectType.Staff)
        {
            return true;
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return await db.AccessGrants.AnyAsync(
            g => g.AccountId == account.Id
                && g.StatusId == StatusIds.AccessGrant.Effective
                && g.EffectiveFrom <= today
                && (g.EffectiveTo == null || g.EffectiveTo >= today),
            cancellationToken);
    }

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
