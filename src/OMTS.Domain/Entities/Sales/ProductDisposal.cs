using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// A document that writes oysters off the stock of products (XHS000001): spoiled, lost or given away. It belongs to one zone, the zone of the harvest lots on its lines. A Draft holds no kg and is removed,
/// never cancelled; a Completed disposal writes its kg on the product ledger and stays editable (its rows are undone and written again); a Cancelled one counts nowhere.
/// </summary>
public class ProductDisposal : AuditableEntity, IHasStatus
{
    public string Code { get; set; } = string.Empty;

    /// <summary>When the oysters left, stored at UTC+7.</summary>
    public DateTimeOffset DisposedAt { get; set; }

    public Guid ZoneId { get; set; }
    public FarmingZone Zone { get; set; } = null!;

    /// <summary>Required to complete.</summary>
    public string? Reason { get; set; }
    public string? Notes { get; set; }

    public int StatusId { get; set; } = StatusIds.ProductDisposal.Draft;
    public Status Status { get; set; } = null!;
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public ICollection<ProductDisposalLine> Lines { get; set; } = [];
}

/// <summary>Kg of one product taken from one harvest lot of the zone of the disposal.</summary>
public class ProductDisposalLine : Entity
{
    public Guid DisposalId { get; set; }
    public ProductDisposal Disposal { get; set; } = null!;
    public int LineNumber { get; set; }
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public Guid HarvestLotId { get; set; }
    public HarvestLot HarvestLot { get; set; } = null!;

    /// <summary>Above 0, at most 3 decimals.</summary>
    public decimal Kg { get; set; }
    public string? Note { get; set; }
}
