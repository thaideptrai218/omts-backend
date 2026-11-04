using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Identity;

/// <summary>
/// E06 – a zone–role pair of a staff account (FARM-05): one fixed role in one zone or in All zones. It is Effective
/// from its grant until it is revoked (with the time, the person and the reason); there are no validity dates.
/// Re-granting creates a new pair; a revoked one is never restored (BR-FARM-08).
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

    /// <summary>The Owner who granted the pair; the time is <see cref="AuditableEntity.CreatedAt"/>.</summary>
    public Guid GrantedByAccountId { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedByAccountId { get; set; }
    public string? RevokeReason { get; set; }

    public int StatusId { get; set; } = StatusIds.AccessGrant.Effective;
    public Status Status { get; set; } = null!;

    public bool IsEffective => StatusId == StatusIds.AccessGrant.Effective;
}
