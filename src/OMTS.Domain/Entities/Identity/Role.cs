using OMTS.Domain.Common;
using OMTS.Domain.Statuses;

namespace OMTS.Domain.Entities;

/// <summary>E04 – Role defined by the Cooperative Owner (Zone Manager, Accountant…) (BR-19).</summary>
public class Role : AuditableEntity, IHasStatus
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;

    public ICollection<RolePermission> Permissions { get; set; } = [];
    public ICollection<AccessGrant> AccessGrants { get; set; } = [];
}
