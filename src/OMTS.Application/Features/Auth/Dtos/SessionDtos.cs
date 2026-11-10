using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Auth.Dtos;

/// <summary>One open login session of the caller, as shown in "Thiết bị đang đăng nhập".</summary>
/// <param name="DeviceKey">Opaque key of the device (browser or app install) the session was opened on; null when the client sent no device id.</param>
/// <param name="MaskedIpAddress">Address of the request that opened the session, with its last part hidden.</param>
/// <param name="LastActiveAt">The last refresh, or the sign-in when the session was never refreshed.</param>
/// <param name="ExpiresAt">When the session can no longer be refreshed unless it is refreshed again.</param>
/// <param name="IsCurrent">The session of the request itself.</param>
public sealed record SessionView(
    Guid Id,
    ClientType ClientType,
    AuthMethod AuthMethod,
    string? DeviceKey,
    string? DeviceName,
    string? UserAgent,
    string? MaskedIpAddress,
    DateTimeOffset SignedInAt,
    DateTimeOffset LastActiveAt,
    DateTimeOffset ExpiresAt,
    bool IsCurrent);

/// <summary>
/// A browser or app install with at least one open session. Sessions without a device key are never grouped: each one is its
/// own entry with a null <see cref="DeviceKey"/>, ended through its session id.
/// </summary>
/// <param name="DeviceKey">Pass it to <c>DELETE api/me/devices/{deviceKey}</c>; null for a session that has no device.</param>
/// <param name="DeviceName">Name, user agent and address are those of the device's newest session.</param>
/// <param name="FirstSignedInAt">The oldest sign-in among the open sessions of the device.</param>
/// <param name="IsCurrent">The request itself comes from this device.</param>
public sealed record DeviceView(
    string? DeviceKey,
    ClientType ClientType,
    AuthMethod AuthMethod,
    string? DeviceName,
    string? UserAgent,
    string? MaskedIpAddress,
    DateTimeOffset FirstSignedInAt,
    DateTimeOffset LastActiveAt,
    bool IsCurrent,
    IReadOnlyList<Guid> SessionIds);

/// <summary>How many sessions a revoke-others call ended.</summary>
public sealed record SessionsRevoked(int Count);
