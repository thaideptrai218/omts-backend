using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Sales;

/// <summary>SALE-01/02/03. The user changed purchase quantities to whole cages on 31/10/2026.
/// This is a request, independent of the old kg-based SalesOrder and of later port weighing.</summary>
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
    public DateOnly? AcceptedPickupDate { get; set; }
    public bool AllowPayLater { get; set; }
    public string? ResponseNote { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public Guid? RespondedByAccountId { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }
}
