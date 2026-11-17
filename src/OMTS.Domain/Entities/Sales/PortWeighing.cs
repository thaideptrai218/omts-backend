using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// One weighing at the port for an accepted request (decision of 09/11/2026): after the harvest each cage is weighed, the Zone Manager records the kg of each cage, the trader confirms
/// the figures, and only then can the invoice be made from it. It may be corrected until the trader confirms. Whether it is confirmed or invoiced is worked out, never a status.
/// </summary>
public class PortWeighing : AuditableEntity
{
    public Guid PurchaseRequestId { get; set; }
    public PurchaseRequest PurchaseRequest { get; set; } = null!;

    public DateTimeOffset WeighedAt { get; set; }

    /// <summary>The Zone Manager or Owner who recorded it.</summary>
    public Guid WeighedByAccountId { get; set; }

    /// <summary>When the trader confirmed the figures: null until then.</summary>
    public DateTimeOffset? ConfirmedAt { get; set; }

    public Guid? ConfirmedByAccountId { get; set; }

    public ICollection<PortWeighingCage> Cages { get; set; } = [];

    /// <summary>The invoices made from it; at most one is Completed.</summary>
    public ICollection<SalesInvoice> Invoices { get; set; } = [];

    public bool IsConfirmed => ConfirmedAt is not null;
}
