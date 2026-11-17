using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Auth.Dtos;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.EmployeeApp;

/// <summary>
/// Refresh, logout and the check of every request of the employee app (LABOR-06, BR-LABOR-02): the same rotation, replay detection and
/// grace period as a staff session, on the sessions of employees only. Logging out ends one session and keeps the device linked, so the
/// next login on that device needs no code (LABOR-06.1).
/// </summary>
public sealed class EmployeeAppSessionService(
    IEmployeeAppRepository app,
    ITokenService tokenService,
    EmployeeAppTokenIssuer issuer,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    // A refresh whose save failed without another rotation having happened (a deadlock victim) is tried again.
    private const int MaxRefreshAttempts = 3;

    public async Task<Result<EmployeeAppTokens>> RefreshAsync(EmployeeAppRefreshRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return AppError.Invalid("Refresh token");
        }

        var now = timeProvider.GetUtcNow();
        var tokenHash = tokenService.HashRefreshToken(request.RefreshToken);
        var session = await app.GetSessionByRefreshTokenHashAsync(tokenHash, cancellationToken);
        if (session is null)
        {
            // The token the last rotation replaced is still answered for its grace period; any other old token is replay, so that session ends.
            if (await app.GetSessionByPreviousRefreshTokenHashAsync(tokenHash, cancellationToken) is { } superseded
                && superseded.IsWithinRefreshGrace(tokenHash, now))
            {
                return await RefreshWithinGraceAsync(superseded, request.RefreshToken, now, cancellationToken);
            }

            return Refused(await RejectReplayedTokenAsync(request.RefreshToken, now, cancellationToken));
        }

        for (var attempt = 1; ; attempt++)
        {
            if (!session.CanRefresh(now))
            {
                return Refused(ReasonOfEnded(session));
            }

            if (await RefusalToRefreshAsync(session, now, cancellationToken) is { } refusal)
            {
                return refusal;
            }

            var lifetime = tokenService.GetEmployeeSessionLifetime();
            var refreshToken = tokenService.CreateRefreshToken(session.Id);
            session.Rotate(tokenService.HashRefreshToken(refreshToken), now, lifetime.RefreshTokenLifetime, lifetime.RefreshGrace);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (ConcurrencyConflictException)
            {
                // Another request rotated the same token first, the session was revoked, or the save was a deadlock victim.
                await unitOfWork.ReloadAsync(session, cancellationToken);
                if (session.RefreshTokenHash == tokenHash)
                {
                    if (attempt < MaxRefreshAttempts)
                    {
                        continue;
                    }

                    throw;
                }

                if (!session.IsWithinRefreshGrace(tokenHash, now))
                {
                    await RevokeAsync(session, now, "Refresh token reuse detected", cancellationToken, SessionRevokeCause.TokenReuse);
                    return Refused(RefreshFailureReason.Reuse);
                }

                return await RefreshWithinGraceAsync(session, request.RefreshToken, now, cancellationToken);
            }

            return issuer.Tokens(session.Employee, session, session.EmployeeDeviceId, refreshToken);
        }
    }

    /// <summary>Ends the current session and keeps the device linked (LABOR-06.1); false when it no longer exists.</summary>
    public async Task<bool> LogoutAsync(Guid employeeId, Guid sessionId, CancellationToken cancellationToken)
    {
        if (await app.GetSessionAsync(sessionId, cancellationToken) is not { } session || session.EmployeeId != employeeId)
        {
            return false;
        }

        await RevokeAsync(session, timeProvider.GetUtcNow(), "Logout", cancellationToken);
        return true;
    }

    /// <summary>Checked on every request: a revoked or expired session, an unlinked device or an employee who is no longer Employed is refused (GFR-02).</summary>
    public Task<bool> IsSessionActiveAsync(Guid employeeId, Guid sessionId, Guid deviceId, CancellationToken cancellationToken) =>
        app.IsSessionActiveAsync(employeeId, sessionId, deviceId, timeProvider.Today(), timeProvider.GetUtcNow(), cancellationToken);

    /// <summary>The replaced token inside its grace period: a new access token, but no new refresh token and no second rotation.</summary>
    private async Task<Result<EmployeeAppTokens>> RefreshWithinGraceAsync(
        EmployeeSession session, string presentedRefreshToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!session.CanRefresh(now))
        {
            return Refused(ReasonOfEnded(session));
        }

        if (await RefusalToRefreshAsync(session, now, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        return issuer.Tokens(session.Employee, session, session.EmployeeDeviceId, presentedRefreshToken, refreshTokenRotated: false);
    }

    /// <summary>A refresh is refused once the device is unlinked or the employee is no longer Employed today; that also ends the session.</summary>
    private async Task<Result<EmployeeAppTokens>?> RefusalToRefreshAsync(EmployeeSession session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (session.EmployeeDevice.IsLinked && EmployeeAppAccess.IsEmployedToday(session.Employee, timeProvider.Today()))
        {
            return null;
        }

        await RevokeAsync(session, now, "The employee may no longer use the employee app", cancellationToken);
        return Refused(RefreshFailureReason.Revoked);
    }

    /// <summary>
    /// A token that is neither current nor within its grace period. A session still usable is ended as possibly stolen (reuse); one that
    /// already ended answers how it ended; a token that names no session is expired.
    /// </summary>
    private async Task<RefreshFailureReason> RejectReplayedTokenAsync(string refreshToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (tokenService.ReadSessionId(refreshToken) is not { } sessionId || await app.GetSessionAsync(sessionId, cancellationToken) is not { } replayed)
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

    private static RefreshFailureReason ReasonOfEnded(EmployeeSession session) => session.RevokedAt is null
        ? RefreshFailureReason.Expired
        : session.RevokeCause == SessionRevokeCause.TokenReuse ? RefreshFailureReason.Reuse : RefreshFailureReason.Revoked;

    private async Task RevokeAsync(
        EmployeeSession session, DateTimeOffset now, string reason, CancellationToken cancellationToken,
        SessionRevokeCause cause = SessionRevokeCause.Revoked)
    {
        session.Revoke(now, reason, cause);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // The session changed meanwhile (rotated or revoked): revoke what it is now.
            await unitOfWork.ReloadAsync(session, cancellationToken);
            session.Revoke(now, reason, cause);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>401 with the reason, as for a staff refresh: <c>expired</c>, <c>reuse</c> or <c>revoked</c>.</summary>
    private static Result<EmployeeAppTokens> Refused(RefreshFailureReason reason) => new AppError(
        ErrorKind.Unauthorized,
        UserMessages.SessionExpired,
        new Dictionary<string, object?>
        {
            ["reason"] = reason switch { RefreshFailureReason.Reuse => "reuse", RefreshFailureReason.Revoked => "revoked", _ => "expired" },
        });
}
