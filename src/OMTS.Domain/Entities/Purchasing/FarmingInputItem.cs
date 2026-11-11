using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>
/// InputItem (PURCHASE-08): an item of the Cooperative Owner's catalogue of farming inputs (oyster seed, buoys, ropes,
/// cages…). Code and name are unique, discontinued items included (GBR-03, BR-PURCHASE-10); the type (<see cref="Group"/>) is
/// fixed once the item is used. Quantities, costs and prices are per base unit; the base unit is fixed once the item has movements.
/// </summary>
public class FarmingInputItem : AuditableEntity, IHasStatus
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public InputItemGroup Group { get; set; }
    /// <summary>The name of the base unit, kept in step with the base row of <see cref="Units"/> (con, kg, bag, piece, roll, m…).</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>The user group ("Nhóm hàng"), optional.</summary>
    public Guid? ItemGroupId { get; set; }
    public InputItemCategory? ItemGroup { get; set; }
    public string? Notes { get; set; }

    /// <summary>Reorder level and ceiling in base units, optional.</summary>
    public decimal? MinQuantity { get; set; }
    public decimal? MaxQuantity { get; set; }

    /// <summary>The cost of one base unit; null until set or taken from the first receipt.</summary>
    public decimal? CostPrice { get; set; }

    /// <summary>The net price of one base unit on the latest Completed receipt that has the item; never typed.</summary>
    public decimal? LastPurchasePrice { get; set; }

    /// <summary>A deleted item: its history stays, no list shows it.</summary>
    public bool IsHidden { get; set; }

    public ICollection<InputItemUnit> Units { get; set; } = [];
    public ICollection<InputItemPhoto> Photos { get; set; } = [];
    /// <summary>Optional note, outside the SRS attributes (kept, gap Q17).</summary>
    public string? Description { get; set; }
    public string? IllustrationUrl { get; set; }
    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;
}

