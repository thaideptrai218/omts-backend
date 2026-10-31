using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>
/// InputItem (PURCHASE-08): an item of the Cooperative Owner's catalogue of farming inputs (oyster seed, buoys, ropes,
/// cages…). Code and name are unique, discontinued items included (GBR-03, BR-PURCHASE-10); group and unit are fixed
/// once the item is used; all Seed items share one unit.
/// </summary>
public class FarmingInputItem : AuditableEntity, IHasStatus
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public InputItemGroup Group { get; set; }
    /// <summary>Unit of quantities on receipt lines (con, kg, bag, piece, roll, m…).</summary>
    public string Unit { get; set; } = string.Empty;
    /// <summary>Optional note, outside the SRS attributes (kept, gap Q17).</summary>
    public string? Description { get; set; }
    public string? IllustrationUrl { get; set; }
    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;
}
