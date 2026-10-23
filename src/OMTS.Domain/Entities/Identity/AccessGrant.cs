using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Identity;

/// <summary>
/// E06 – A zone–role pair assigned to an account. Effective permissions are the union of the roles'
/// permissions in the zone. Re-granting creates a new grant; a revoked one is never restored (BR-19).
/// </summary>
public class AccessGrant : AuditableEntity, IHasStatus
{
    public Guid AccountId { get; set; }
    public Account Account { get; set; } = null!;

    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;

    /// <summary>Null means all zones.</summary>
    public Guid? ZoneId { get; set; }
    public FarmingZone? Zone { get; set; }

    public AccessScopeType ScopeType { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    /// <summary>“Expired” is derived once this date has passed; the status stays Effective.</summary>
    public DateOnly? EffectiveTo { get; set; }

    public Guid GrantedByAccountId { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedByAccountId { get; set; }
    public string? RevokeReason { get; set; }

    public int StatusId { get; set; } = StatusIds.AccessGrant.Effective;
    public Status Status { get; set; } = null!;

    public bool IsEffectiveOn(DateOnly date) =>
        StatusId == StatusIds.AccessGrant.Effective && EffectiveFrom <= date && (EffectiveTo is null || EffectiveTo >= date);
}
