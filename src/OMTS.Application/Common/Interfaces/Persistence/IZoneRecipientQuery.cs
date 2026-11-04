using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Who must hear about something happening in a zone or the cooperative (expiry reminders, water alerts, overdue reminders).</summary>
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

    /// <summary>Active Cooperative Owner accounts only (e.g. the expiry reminders of GFR-03).</summary>
    Task<IReadOnlyList<Guid>> GetCooperativeOwnersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Active staff holding an Effective Zone Manager pair on <paramref name="zoneId"/> or at All zones (BR-FARM-08); when
    /// the zone has none, every active Cooperative Owner instead (BR-CYCLE-07, GFR-04).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetZoneManagersOrOwnersAsync(Guid zoneId, CancellationToken cancellationToken);

    /// <summary>
    /// Active Cooperative Owner accounts, plus active Staff accounts with an Effective pair of one of
    /// <paramref name="staffRoles"/> in any zone (records with no zone, BR-FARM-08): e.g. the Accountant and the Owner of
    /// MSG-PURCHASE-08. An empty list gives the Owners only.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetOwnersAndRolesAsync(IReadOnlyCollection<StaffRole> staffRoles, CancellationToken cancellationToken);
}
