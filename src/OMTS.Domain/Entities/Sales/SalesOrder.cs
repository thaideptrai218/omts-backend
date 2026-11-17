using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// An order of goods (Đặt hàng, DH000001) that every sales invoice is made from. A Draft order is being agreed; it becomes Delivering when its first invoice is saved and Completed when it is finished by hand or
/// everything is delivered; a Cancelled order counts nowhere. The customer pays an advance on it with a customer payment that names the order (<see cref="CustomerPayment.SalesOrderId"/>): that money is reserved
/// for the order's own invoices while the order is Draft or Delivering. The order has no zone: the harvest plan decides which zones harvest for it, and an invoice made from it takes the zone of its first lot. A walk-in buyer has no profile (<see cref="CustomerId"/> is null) and pays no advance.
/// </summary>
public class SalesOrder : AuditableEntity, IHasStatus
{
    /// <summary>DH000123: the user may type another code, unique regardless of letter case, drafts and cancelled orders included.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>When the order was placed, at UTC+7.</summary>
    public DateTimeOffset OrderedAt { get; set; }

    /// <summary>The business date (UTC+7) of <see cref="OrderedAt"/>, for period filters.</summary>
    public DateOnly OrderDate { get; set; }

    /// <summary>When the buyer will collect the goods, at UTC+7.</summary>
    public DateTimeOffset? PickupAt { get; set; }

    /// <summary>The business date of <see cref="PickupAt"/>: filters and the days an order has waited.</summary>
    public DateOnly? PickupDate { get; set; }

    /// <summary>Kept only on orders saved before an order lost its zone; a new order has none. The zone of a delivery is decided by the harvest plan and by the lot of each invoice line.</summary>
    public Guid? ZoneId { get; set; }
    public FarmingZone? Zone { get; set; }

    /// <summary>None for a walk-in buyer.</summary>
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>The trader's purchase request this order was made from; one request makes at most one order.</summary>
    public Guid? PurchaseRequestId { get; set; }
    public PurchaseRequest? PurchaseRequest { get; set; }

    public SalesOrderSource Source { get; set; }

    /// <summary>Who took the order ("Người nhận đặt").</summary>
    public Guid ReceivedByAccountId { get; set; }
    public Guid CreatedByAccountId { get; set; }

    /// <summary>Whole VND, 0 or more, never above the subtotal.</summary>
    public decimal DiscountAmount { get; set; }
    public bool DiscountDecidedByOwner { get; set; }

    /// <summary>The sum of the line amounts.</summary>
    public decimal Subtotal { get; set; }

    /// <summary>Subtotal minus the discount, never below 0.</summary>
    public decimal Total { get; set; }

    public string? Note { get; set; }

    /// <summary>A repeated key returns the first order and creates nothing.</summary>
    public string? DeduplicationKey { get; set; }

    public int StatusId { get; set; } = StatusIds.SalesOrder.Draft;
    public Status Status { get; set; } = null!;

    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? CompletedByAccountId { get; set; }

    /// <summary>True when the Owner finished the order ("Kết thúc"); false when the last delivery completed it.</summary>
    public bool CompletedManually { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledByAccountId { get; set; }
    public string? CancelReason { get; set; }

    public ICollection<SalesOrderLine> Lines { get; set; } = [];
}

/// <summary>One product of an order, counted in one of its units (kg, cage, box). The unit, its factor and the list price are copied, so later changes to the product do not touch the order.</summary>
public class SalesOrderLine : Entity
{
    public Guid OrderId { get; set; }
    public SalesOrder Order { get; set; } = null!;

    /// <summary>1, 2, … in the order entered.</summary>
    public int LineNumber { get; set; }

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>The shared unit of the line and its code, name and kg factor as they were; no foreign key, so the unit can leave the product later.</summary>
    public Guid UnitId { get; set; }
    public string UnitCode { get; set; } = string.Empty;
    public string UnitName { get; set; } = string.Empty;
    public decimal Factor { get; set; } = 1m;

    /// <summary>Above 0, at most 3 decimals, in the unit of the line.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Whole VND per unit of the line: the list price, or the price the Owner decided.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>The sale price of the unit when the line was saved.</summary>
    public decimal ListPrice { get; set; }

    /// <summary>Quantity × unit price, rounded half up to the dong.</summary>
    public decimal Amount { get; set; }

    public string? Note { get; set; }
}
