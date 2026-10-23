using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Identity;

/// <summary>
/// Technical entity (SDS): a device registered for push notifications with its Expo push token (ACS-07).
/// Push is an eligible channel only while the account has at least one device that allows notifications.
/// </summary>
public class PushDevice : Entity
{
    public Guid AccountId { get; set; }
    public Account Account { get; set; } = null!;

    public string Token { get; set; } = string.Empty;

    /// <summary>"ios", "android" or "web".</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>False when the user declined push notifications on the device (MSG22).</summary>
    public bool PermissionGranted { get; set; }

    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
