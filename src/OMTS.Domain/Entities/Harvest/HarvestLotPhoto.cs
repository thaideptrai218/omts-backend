using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Harvest;

/// <summary>A photo of a harvest (HARVEST-01 step 4): each holds one slot of 1 to <see cref="HarvestLot.MaxPhotos"/>, and the database refuses a slot out of range or taken.</summary>
public class HarvestLotPhoto : AuditableEntity
{
    public Guid LotId { get; set; }
    public HarvestLot Lot { get; set; } = null!;

    /// <summary>The slot, also the display order.</summary>
    public int SortOrder { get; set; }

    /// <summary>Storage key of the file; never a public link (reads use signed URLs).</summary>
    public string FileKey { get; set; } = string.Empty;
}
