using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// A notification that a trader sends in the app, not an order (SALE-01, BR-SALE-01): one product, how many whole cages and the
/// pickup date (decision of 08/11/2026: requests are in cages; the kg of each cage is entered when it is weighed at the port). It
/// has no price and reserves no oysters. "Fulfilled" is not a status: it is worked out from the invoices linked to the request.
/// </summary>
public class PurchaseRequest : AuditableEntity, IHasStatus
{
    public string Code { get; set; } = string.Empty;
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public string ProductName { get; set; } = string.Empty;
    public int RequestedCages { get; set; }
    public DateOnly PickupDate { get; set; }
    public string? Note { get; set; }
    public Guid ClientOperationId { get; set; }
    public int StatusId { get; set; } = StatusIds.PurchaseRequest.AwaitingResponse;
    public Status Status { get; set; } = null!;
    public int? AcceptedCages { get; set; }
    /// <summary>The date the Owner agreed; the trader's date is <see cref="PickupDate"/>.</summary>
    public DateOnly? AgreedPickupDate { get; set; }
    /// <summary>Lets the Zone Manager save the linked invoices as Pay later (BR-SALE-07).</summary>
    public bool? PayLaterAgreed { get; set; }
    public string? ResponseNote { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public Guid? RespondedByAccountId { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset SentAt { get; set; }

    /// <summary>
    /// When the Cooperative Owner marked the harvest of the request done (decision of 11/11/2026): no harvest lot is added to it after this, and only then is it
    /// weighed at the port. Not a status: it is cleared again while the request has no weighing.
    /// </summary>
    public DateTimeOffset? HarvestCompletedAt { get; set; }
    public Guid? HarvestCompletedByAccountId { get; set; }
    public int? PlannedCages { get; set; }
    public DateOnly? WorkDate { get; set; }
    public DateOnly? PlannedPickupDate { get; set; }
    public int? CagesPerEmployeePerDay { get; set; }
    public string? StaffingNote { get; set; }
    public ICollection<PurchaseShiftInvitation> ShiftInvitations { get; set; } = [];
    public WorkDayPlan? WorkDayPlan { get; set; }
}
