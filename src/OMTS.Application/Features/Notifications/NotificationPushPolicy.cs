namespace OMTS.Application.Features.Notifications;

/// <summary>Whether a notification of a type is also pushed to the recipient's devices (BR-ACCOUNT-11).</summary>
public enum NotificationPushPolicy
{
    /// <summary>Pushed unless the account turned push off for the type (ACCOUNT-07).</summary>
    Default,

    /// <summary>Mandatory: always pushed, push cannot be turned off, shown as always pushed.</summary>
    AlwaysPush,

    /// <summary>Kept in the list only, never pushed and not in the preferences (BR-LABOR-04: lock reminder, payroll send-back).</summary>
    NeverPush,
}
