using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Labor;

/// <summary>E38 – Intermediary the cooperative contacts and pays for seasonal work or whole tasks (BR-20).</summary>
public class Contractor : AuditableEntity, IHasStatus
{
    public string Name { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? OtherContacts { get; set; }

    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;
}
