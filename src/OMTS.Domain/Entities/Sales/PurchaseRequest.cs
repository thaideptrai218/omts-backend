using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// A notification that a trader sends in the app, not an order (SALE-01, BR-SALE-01): one product, about how many kg and the
/// pickup date. It has no price, no code (the SRS gives it no business identity) and reserves no oysters. It cannot be edited:
/// the Cooperative Owner answers once (SALE-03, BR-SALE-02) and the trader may cancel (SALE-02). "Fulfilled" is not a status:
/// it is worked out from the invoices linked to the request (<c>IPurchaseRequestFulfilment</c>).
/// </summary>
public class PurchaseRequest : AuditableEntity, IHasStatus
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>The approximate kg the trader asks for, above 0 (PM 03/11: requests are in kg, not cages).</summary>
    public decimal RequestedKg { get; set; }

    /// <summary>The date the trader asked for; the Owner's date is <see cref="AgreedPickupDate"/>.</summary>
    public DateOnly PickupDate { get; set; }

    public string? Note { get; set; }
    public DateTimeOffset SentAt { get; set; }

    /// <summary>Above 0 and not more than <see cref="RequestedKg"/>; set when Accepted, and kept if the trader cancels later.</summary>
    public decimal? AcceptedKg { get; set; }
    public DateOnly? AgreedPickupDate { get; set; }

    /// <summary>Lets the Zone Manager save the linked invoices as Pay later (BR-SALE-07), with no limit of kg.</summary>
    public bool? PayLaterAgreed { get; set; }

    /// <summary>Optional when Accepted; the reason when Declined.</summary>
    public string? ResponseNote { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public Guid? RespondedByAccountId { get; set; }

    public string? CancelReason { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }

    public int StatusId { get; set; } = StatusIds.PurchaseRequest.AwaitingResponse;
    public Status Status { get; set; } = null!;
}
