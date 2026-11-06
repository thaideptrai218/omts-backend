using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Labor;

/// <summary>A kind of work, for example harvesting or hanging seed, paid at its own hourly rate (LABOR-11, BR-LABOR-12).</summary>
public class JobType : AuditableEntity, IHasStatus
{
    public string Name { get; set; } = string.Empty;

    /// <summary>In Use or Discontinued (GBR-01).</summary>
    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;

    public ICollection<PayRate> Rates { get; set; } = [];
}
