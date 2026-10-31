using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Notifications;

/// <summary>The roles notification types are sent to: the three fixed roles (BR-FARM-07) and traders.</summary>
public enum NotificationRole
{
    CooperativeOwner,
    ZoneManager,
    Accountant,
    Trader,
}

/// <summary>The notification roles an account holds.</summary>
public static class NotificationRoles
{
    /// <summary>
    /// The Owner and traders by account type; staff by the fixed roles of their Effective zone–role pairs, so a staff
    /// account without a pair holds none.
    /// </summary>
    public static IReadOnlySet<NotificationRole> Of(SubjectType subjectType, IEnumerable<ZoneGrant> effectiveGrants) => subjectType switch
    {
        SubjectType.CooperativeOwner => new HashSet<NotificationRole> { NotificationRole.CooperativeOwner },
        SubjectType.Trader => new HashSet<NotificationRole> { NotificationRole.Trader },
        SubjectType.Staff => effectiveGrants
            .Select(g => FixedRoles.FindByCode(g.RoleCode)?.Role)
            .OfType<StaffRole>()
            .Select(r => r == StaffRole.ZoneManager ? NotificationRole.ZoneManager : NotificationRole.Accountant)
            .ToHashSet(),
        _ => new HashSet<NotificationRole>(),
    };
}
