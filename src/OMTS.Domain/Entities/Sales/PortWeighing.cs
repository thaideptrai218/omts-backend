using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// E26 – Weight confirmed by the buyer at the port; the billable quantity of a delivery (BR-11).
/// A post-sale weight correction needs a new slip confirmed by the trader (BR-23).
/// </summary>
public class PortWeighing : AuditableEntity
{
    public Guid OrderId { get; set; }
    public SalesOrder Order { get; set; } = null!;

    public string? Port { get; set; }
    public DateTimeOffset WeighedAt { get; set; }
    public decimal ConfirmedKg { get; set; }

    /// <summary>Basis of the buyer's confirmation.</summary>
    public string? BuyerConfirmation { get; set; }

    public List<string> EvidenceUrls { get; set; } = [];
}
