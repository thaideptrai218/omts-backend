using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>
/// E13 – Farming input catalogue item kept by the Cooperative Owner (INP-02): oyster seed types (group Seed,
/// used by cultivation plans and seed lots), floats, ropes, cages… Code, group and unit are fixed once used.
/// </summary>
public class FarmingInputItem : AuditableEntity, IHasStatus
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public InputItemGroup Group { get; set; }
    /// <summary>Unit of quantities on receipt lines (kg, bag, piece, roll, m…).</summary>
    public string Unit { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? IllustrationUrl { get; set; }
    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;
}
