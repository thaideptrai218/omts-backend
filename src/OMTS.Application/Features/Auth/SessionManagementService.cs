using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Auth.Dtos;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Auth;

/// <summary>
/// The caller's own open sessions and the devices they belong to: list them, end one, end the others, end a device.
/// Only the caller's account is ever read or changed; a session of another account is simply not found. Revocations are
/// tracked changes saved here, which is what publishes them to the revocation store.
/// </summary>
public sealed class SessionManagementService(ILoginSessionRepository sessions, IUnitOfWork unitOfWork, TimeProvider timeProvider)
{
    private const int MaxSaveAttempts = 3;

    /// <summary>Open sessions that can still be refreshed, newest first.</summary>
    public async Task<IReadOnlyList<SessionView>> ListSessionsAsync(Guid accountId, Guid? currentSessionId, CancellationToken cancellationToken)
    {
        var open = await ListOpenAsync(accountId, cancellationToken);
        return open.Select(s => ToView(s, currentSessionId)).ToList();
    }

    /// <summary>Ends one session of the caller, the current one included. 404 for a session that is not the caller's; an own session that already ended is a success.</summary>
    public async Task<Result<Done>> RevokeSessionAsync(Guid accountId, Guid sessionId, CancellationToken cancellationToken)
    {
        if (await sessions.GetAsync(sessionId, accountId, cancellationToken) is not { } session)
        {
            return AppError.NotFound;
        }

        await RevokeAsync([session], "Session revoked by user", cancellationToken);
        return Done.Value;
    }

    /// <summary>Ends every open session of the caller except the current one.</summary>
    public async Task<SessionsRevoked> RevokeOthersAsync(Guid accountId, Guid? currentSessionId, CancellationToken cancellationToken)
    {
        var others = (await sessions.ListUsableAsync(accountId, timeProvider.GetUtcNow(), cancellationToken))
            .Where(s => s.Id != currentSessionId)
            .ToList();
        await RevokeAsync(others, "Other sessions revoked by user", cancellationToken);
        return new SessionsRevoked(others.Count);
    }

    /// <summary>Open sessions grouped by device, most recently active first.</summary>
    public async Task<IReadOnlyList<DeviceView>> ListDevicesAsync(Guid accountId, Guid? currentSessionId, CancellationToken cancellationToken)
    {
        var open = await ListOpenAsync(accountId, cancellationToken);
        var devices = new List<DeviceView>();
        foreach (var group in open.Where(s => s.DeviceKey is not null).GroupBy(s => s.DeviceKey))
        {
            devices.Add(ToDeviceView(group.Key, [.. group], currentSessionId));
        }

        // A session that names no device is never grouped with another.
        devices.AddRange(open.Where(s => s.DeviceKey is null).Select(s => ToDeviceView(null, [s], currentSessionId)));
        return devices.OrderByDescending(d => d.LastActiveAt).ToList();
    }

    /// <summary>
    /// Ends the caller's open sessions on a device. Repeating it, or naming a device the caller has no open session on, changes
    /// nothing and is still a success: the answer never tells whether the key exists for anyone else.
    /// </summary>
    public async Task<Result<Done>> RevokeDeviceAsync(Guid accountId, string deviceKey, CancellationToken cancellationToken)
    {
        var onDevice = (await sessions.ListUsableAsync(accountId, timeProvider.GetUtcNow(), cancellationToken))
            .Where(s => s.DeviceKey == deviceKey)
            .ToList();
        await RevokeAsync(onDevice, "Device revoked by user", cancellationToken);
        return Done.Value;
    }

    private async Task<List<LoginSession>> ListOpenAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return (await sessions.ListUsableAsync(accountId, now, cancellationToken)).Where(s => s.CanRefresh(now)).ToList();
    }

    private async Task RevokeAsync(IReadOnlyList<LoginSession> toRevoke, string reason, CancellationToken cancellationToken)
    {
        if (toRevoke.Count == 0)
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            var now = timeProvider.GetUtcNow();
            foreach (var session in toRevoke)
            {
                session.Revoke(now, reason);
            }

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (ConcurrencyConflictException) when (attempt < MaxSaveAttempts)
            {
                // A refresh rotated one of the sessions meanwhile: re-read them and revoke what the database holds now.
                foreach (var session in toRevoke)
                {
                    await unitOfWork.ReloadAsync(session, cancellationToken);
                }
            }
        }
    }

    private static SessionView ToView(LoginSession session, Guid? currentSessionId) => new(
        session.Id,
        session.ClientType,
        session.AuthMethod,
        session.DeviceKey,
        session.DeviceName,
        session.UserAgent,
        MaskedIp(session),
        session.CreatedAt,
        session.LastRefreshedAt ?? session.CreatedAt,
        session.RefreshTokenExpiresAt,
        session.Id == currentSessionId);

    private static DeviceView ToDeviceView(string? deviceKey, IReadOnlyList<LoginSession> group, Guid? currentSessionId)
    {
        var newest = group.OrderByDescending(s => s.CreatedAt).First();
        return new DeviceView(
            deviceKey,
            newest.ClientType,
            newest.AuthMethod,
            newest.DeviceName,
            newest.UserAgent,
            MaskedIp(newest),
            group.Min(s => s.CreatedAt),
            group.Max(s => s.LastRefreshedAt ?? s.CreatedAt),
            group.Any(s => s.Id == currentSessionId),
            group.Select(s => s.Id).ToList());
    }

    private static string? MaskedIp(LoginSession session) =>
        string.IsNullOrEmpty(session.IpAddress) ? null : Masking.IpAddress(session.IpAddress);
}
