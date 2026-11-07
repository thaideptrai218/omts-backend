using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Identity;

/// <summary>
/// E04 – one of the fixed staff roles, Zone Manager or Accountant (BR-FARM-07). The table holds exactly these two rows,
/// found by <see cref="Code"/> and created at deployment; roles are not created, edited or discontinued in OMTS, and
/// their permissions are the static matrix of SRS §I.4.4 (<c>FixedRoles</c>), not stored rows.
/// </summary>
public class Role : AuditableEntity
{
    public const int CodeMaxLength = 30;

    /// <summary>ZONE_MANAGER or ACCOUNTANT; unique.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public ICollection<AccessGrant> AccessGrants { get; set; } = [];
}
