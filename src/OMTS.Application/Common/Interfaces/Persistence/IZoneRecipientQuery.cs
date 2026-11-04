using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Who must hear about something happening in a zone (license reminders, water alerts, overdue reminders).</summary>
public interface IZoneRecipientQuery
{
    /// <summary>
    /// Active Cooperative Owner accounts, plus active Staff accounts holding an Effective zone–role pair on
    /// <paramref name="zoneId"/> or at All zones whose fixed role allows <paramref name="action"/> on
    /// <paramref name="functionCode"/> (for a function on records with no zone, a pair in any zone). Revoked pairs
    /// never count.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetRecipientsAsync(
        Guid zoneId, string functionCode, PermissionAction action, CancellationToken cancellationToken);

    /// <summary>
    /// Active staff holding an Effective Zone Manager pair on <paramref name="zoneId"/> or at All zones (BR-FARM-08); when
    /// the zone has none, every active Cooperative Owner instead (GFR-04).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetZoneManagersOrOwnersAsync(Guid zoneId, CancellationToken cancellationToken);
}
