using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Who must hear about something happening in a zone (license reminders, water alerts, overdue reminders).</summary>
public interface IZoneRecipientQuery
{
    /// <summary>
    /// Active Cooperative Owner accounts, plus active Staff accounts holding an effective zone–role grant on
    /// <paramref name="zoneId"/> or at All zones whose role allows <paramref name="action"/> on
    /// <paramref name="functionCode"/>. Revoked or expired grants and discontinued roles never count.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetRecipientsAsync(
        Guid zoneId, string functionCode, PermissionAction action, CancellationToken cancellationToken);
}
