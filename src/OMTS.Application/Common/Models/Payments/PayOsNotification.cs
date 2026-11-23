namespace OMTS.Application.Common.Models.Payments;

/// <summary>A payOS notification as read: whether its signature is that of the checksum key, and the transfer it carries when it is.</summary>
public sealed record PayOsNotification(bool Authentic, PayOsTransfer? Transfer)
{
    public static readonly PayOsNotification NotAuthentic = new(false, null);
}
