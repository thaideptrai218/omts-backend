namespace OMTS.Application.Features.Accounts.Dtos;

/// <summary>One notification type the account can receive (SCR-ACCOUNT-06).</summary>
/// <param name="Type">Code of the type, e.g. WATER_ALERT.</param>
/// <param name="Name">English name of the type.</param>
/// <param name="Mandatory">Always pushed; <paramref name="Push"/> is true and cannot be changed.</param>
/// <param name="Push">Whether a new notification of the type is also pushed to the user's devices.</param>
public sealed record NotificationTypePreferenceView(string Type, string Name, bool Mandatory, bool Push);

/// <param name="Types">The types offered to the roles the account holds; types that are never pushed are not offered.</param>
/// <param name="PushAllowedOnDevice">
/// At least one device of the account, registered by a session that is still logged in, allows notifications from OMTS.
/// </param>
public sealed record NotificationPreferencesView(IReadOnlyList<NotificationTypePreferenceView> Types, bool PushAllowedOnDevice);

public sealed record NotificationPushChange(string? Type, bool Push);

/// <param name="Items">The types to change; types left out keep their setting.</param>
public sealed record SaveNotificationPreferencesRequest(IReadOnlyList<NotificationPushChange>? Items);

/// <param name="Message">MSG-GEN-05.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
/// <param name="Notice">MSG-ACCOUNT-21 when push was turned on but no device allows notifications; the choice is saved.</param>
/// <param name="NoticeCode">The code of <paramref name="Notice"/>.</param>
public sealed record NotificationPreferencesSaved(
    IReadOnlyList<NotificationTypePreferenceView> Types,
    bool PushAllowedOnDevice,
    string Message,
    string MessageCode,
    string? Notice,
    string? NoticeCode);
