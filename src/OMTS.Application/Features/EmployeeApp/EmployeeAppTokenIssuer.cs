using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.EmployeeApp;

/// <summary>Opens employee app sessions and builds the token answer of a login or a refresh (LABOR-06).</summary>
public sealed class EmployeeAppTokenIssuer(ITokenService tokenService, IEmployeeAppRepository app, TimeProvider timeProvider)
{
    /// <summary>One attendance QR code lasts one minute (BR-LABOR-06: "replaced every 60 seconds so a photo cannot be used later").</summary>
    public const int QrStepSeconds = 60;

    /// <summary>A new session on the device, added to the unit of work (the caller saves), and the refresh token whose hash it stores.</summary>
    public (EmployeeSession Session, string RefreshToken) OpenSession(Guid employeeId, Guid deviceId, DateTimeOffset now)
    {
        var lifetime = tokenService.GetEmployeeSessionLifetime();
        var session = new EmployeeSession
        {
            EmployeeId = employeeId,
            EmployeeDeviceId = deviceId,
            CreatedAt = now,
            RefreshTokenExpiresAt = now + lifetime.RefreshTokenLifetime,
            AbsoluteExpiresAt = now + lifetime.MaxLifetime,
        };
        var refreshToken = tokenService.CreateRefreshToken(session.Id);
        session.RefreshTokenHash = tokenService.HashRefreshToken(refreshToken);
        app.Add(session);
        return (session, refreshToken);
    }

    /// <param name="employee">With its type periods, for the type shown.</param>
    public EmployeeAppTokens Tokens(
        Employee employee, EmployeeSession session, Guid deviceId, string refreshToken, bool refreshTokenRotated = true, string? deviceSecret = null)
    {
        var now = timeProvider.GetUtcNow();
        var access = tokenService.CreateEmployeeAccessToken(employee, session.Id, deviceId, session.AbsoluteExpiresAt);
        return new EmployeeAppTokens(
            access.Token,
            access.ExpiresAt,
            refreshToken,
            session.RefreshTokenExpiresAt,
            refreshTokenRotated,
            new EmployeeAppProfile(employee.Id, employee.FullName, EmployeeTypePeriod.TypeOn(employee.TypePeriods, BusinessClock.ToBusinessDate(now))),
            deviceId,
            deviceSecret,
            tokenService.DeriveEmployeeQrKey(deviceId),
            QrStepSeconds,
            now);
    }
}
