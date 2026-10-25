using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Identity;

/// <summary>
/// Technical entity (SDS): a device registered for push notifications with its Expo push token (ACS-07).
/// The device receives push only while the login session that registered it is still logged in, so logging out of
/// one device (or any revocation of its session) stops its notifications. Push is an eligible channel only
/// while the account has at least one such device that allows notifications.
/// </summary>
public class PushDevice : Entity
{
    public Guid AccountId { get; set; }
    public Account Account { get; set; } = null!;

    /// <summary>Session of the latest registration; empty for devices registered before sessions were bound.</summary>
    public Guid? LoginSessionId { get; set; }
    public LoginSession? LoginSession { get; set; }

    public string Token { get; set; } = string.Empty;

    /// <summary>"ios", "android" or "web".</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>False when the user declined push notifications on the device (MSG22).</summary>
    public bool PermissionGranted { get; set; }

    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
