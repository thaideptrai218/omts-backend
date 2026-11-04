using OMTS.Domain.Common;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Cultivation;

/// <summary>
/// The quantity of one material item used to repair a raft, with the repair reason (BR-CYCLE-03). It has no price, no code and no status. Its cycle is not
/// stored: it belongs to the cycle of the raft that was Open on its date, and a date with no such cycle puts it in a separate group. A date inside a closed
/// cycle of the raft is refused, and a record dated inside one is locked (BR-CYCLE-04).
/// </summary>
public class MaterialUsage : AuditableEntity
{
    /// <summary>The group an item must be in to be used here (BR-CYCLE-03): the one name in this feature for the catalogue group of materials.</summary>
    public const InputItemGroup ItemGroup = InputItemGroup.RaftMaterial;

    public const int MaxRepairReasonLength = 500;

    public Guid RaftId { get; set; }
    public Raft Raft { get; set; } = null!;

    /// <summary>The repair date: not later than today and outside the closed cycles of the raft.</summary>
    public DateOnly UsageDate { get; set; }

    /// <summary>Free text, for example broken buoy or snapped rope. Date and reason together make one repair on the screen.</summary>
    public string RepairReason { get; set; } = string.Empty;

    public Guid ItemId { get; set; }
    public FarmingInputItem Item { get; set; } = null!;

    /// <summary>Greater than 0, in the unit of the item.</summary>
    public decimal Quantity { get; set; }

    public Guid RecordedByAccountId { get; set; }
}
