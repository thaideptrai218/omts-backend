using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Notifications;

/// <summary>One notification type of the catalogue (<see cref="NotificationTypes.All"/>).</summary>
/// <param name="Name">English name shown in the preferences (SCR-ACCOUNT-06).</param>
/// <param name="Roles">Who receives the type; the preferences of an account offer the types of the roles it holds.</param>
/// <param name="Open">How opening the related record re-checks the access of the user (GFR-02, BR-ACCOUNT-12).</param>
public sealed record NotificationTypeDefinition(
    string Code, string Name, NotificationPushPolicy PushPolicy, IReadOnlyList<NotificationRole> Roles, NotificationOpenAccess Open)
{
    /// <summary>Always pushed; the push cannot be turned off (BR-ACCOUNT-11).</summary>
    public bool IsMandatory => PushPolicy == NotificationPushPolicy.AlwaysPush;
}

/// <summary>
/// The right needed to open the related record of a notification: <see cref="Action"/> on <see cref="Function"/> in
/// the zone of the record, or the Cooperative Owner only when <see cref="Function"/> is null (Owner-only use cases
/// such as FARM-03 and ACCOUNT-09).
/// </summary>
public sealed record NotificationOpenAccess(string? Function, PermissionAction Action)
{
    public static readonly NotificationOpenAccess OwnerOnly = new(null, PermissionAction.View);

    public static NotificationOpenAccess InZone(string function, PermissionAction action) => new(function, action);
}
