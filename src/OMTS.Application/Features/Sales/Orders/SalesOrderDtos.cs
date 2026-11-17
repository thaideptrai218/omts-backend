using OMTS.Application.Features.Sales.Payments;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Orders;

/// <summary>How the customer pays an advance: cash, a transfer already received, or a VietQR code that waits for the transfer.</summary>
public enum SalesOrderAdvanceMethod
{
    Cash,
    OtherTransfer,
    VietQr,
}

/// <summary>What the caller may do with an order now; the client shows exactly these buttons.</summary>
public enum SalesOrderAction
{
    /// <summary>Change the order (a Draft or Delivering one).</summary>
    Edit,

    /// <summary>"Kết thúc": a Delivering order becomes Completed.</summary>
    Finish,

    /// <summary>Cancel with a reason; not offered while an invoice that is not cancelled was made from the order.</summary>
    Cancel,

    /// <summary>Take an advance on the order.</summary>
    AddAdvance,

    /// <summary>"Xử lý đặt hàng": make an invoice from the order.</summary>
    Process,

    /// <summary>"Lập kế hoạch thu hoạch" from the order (Owner).</summary>
    PlanHarvest,

    /// <summary>Print the order.</summary>
    Print,
}

/// <summary>A line as entered. <paramref name="UnitPrice"/> empty: the list price of the unit.</summary>
/// <param name="UnitId">The unit of the line: one of the units of the product.</param>
/// <param name="Quantity">In the unit of the line, above 0, at most 3 decimals.</param>
/// <param name="UnitPrice">Whole VND per unit of the line; a price other than the list price needs the Owner (<paramref name="PriceDecidedByOwner"/>).</param>
public sealed record SalesOrderLineInput(Guid? ProductId, Guid? UnitId, decimal? Quantity, decimal? UnitPrice = null, bool PriceDecidedByOwner = false, string? Note = null);

/// <summary>The advance paid on a new order, or added later. Cash is Awaiting confirmation until the Accountant confirms it; a transfer needs its bank reference; VietQr makes a code that waits for the transfer.</summary>
/// <param name="CashAccountId">The Bank account of the cash book a transfer came into; required for VietQr.</param>
/// <param name="CollectedById">Who collected the money; the caller by default.</param>
/// <param name="PaidAt">When the money was paid, never later than now; now by default.</param>
/// <param name="DeduplicationKey">Used when the advance is added later: a repeated key returns the first advance.</param>
public sealed record SalesOrderAdvanceInput(
    decimal? Amount,
    SalesOrderAdvanceMethod? Method,
    Guid? CashAccountId = null,
    string? BankReference = null,
    Guid? CollectedById = null,
    DateTimeOffset? PaidAt = null,
    string? Note = null,
    string? DeduplicationKey = null,
    bool ConfirmDuplicateBankReference = false);

/// <summary>
/// Saves an order. The customer is chosen or the buyer is a walk-in (<paramref name="WalkInCustomer"/> with no <paramref name="CustomerId"/>), never assumed. A purchase request (Owner only) makes the order
/// come from the app. <paramref name="Code"/>: 1–30 ASCII letters, digits, "-", "_" or "."; unique regardless of case; empty = the next DH code (or the stored one on an update).
/// An order has no zone: the harvest plan decides which zones harvest for it (a <c>zoneId</c> sent is ignored). The advance is a new payment, taken in the same save when the order is created or updated; add more with the
/// advance endpoint. An update replaces the lines and needs the row version read.
/// </summary>
/// <param name="Source">Required, except that an order made from a purchase request is always App and a Zone Manager's is always AtPort.</param>
/// <param name="ReceivedById">Who took the order ("Người nhận đặt"); the caller by default.</param>
/// <param name="OrderedAt">When the order was placed, never later than now; now by default (the stored time on an update).</param>
/// <param name="PickupAt">When the buyer will collect the goods.</param>
/// <param name="Discount">Whole VND, 0 or more, at most the subtotal; above 0 needs the Owner (<paramref name="DiscountDecidedByOwner"/>).</param>
public sealed record SaveSalesOrderRequest(
    string? Code,
    Guid? CustomerId,
    bool WalkInCustomer,
    Guid? PurchaseRequestId,
    SalesOrderSource? Source,
    Guid? ReceivedById,
    DateTimeOffset? OrderedAt,
    DateTimeOffset? PickupAt,
    string? Note,
    decimal Discount,
    bool DiscountDecidedByOwner,
    IReadOnlyList<SalesOrderLineInput?>? Lines,
    SalesOrderAdvanceInput? Advance = null,
    string? DeduplicationKey = null,
    string? RowVersion = null);

public sealed record FinishSalesOrderRequest(string? RowVersion = null);

public sealed record CancelSalesOrderRequest(string? Reason, string? RowVersion = null);

public sealed record SalesOrderCustomerView(Guid Id, string Code, string Name, string? PhoneNumber);

/// <summary>The request an order was made from.</summary>
public sealed record SalesOrderRequestView(Guid Id, DateTimeOffset SentAt, int StatusId, string Status);

public sealed record SalesOrderLineView(
    Guid Id,
    int LineNumber,
    Guid ProductId,
    string ProductCode,
    string ProductName,
    Guid UnitId,
    string UnitCode,
    string UnitName,
    decimal Factor,
    decimal Quantity,
    decimal UnitPrice,
    decimal ListPrice,
    decimal Amount,
    decimal DeliveredQuantity,
    string? Note);

/// <summary>What is delivered of what was ordered in one unit: "60/100 kg".</summary>
public sealed record SalesOrderProgressView(Guid UnitId, string UnitCode, string UnitName, decimal Ordered, decimal Delivered);

/// <summary>An advance paid on an order: a customer payment. <paramref name="Reserved"/> is true while the order holds it for its own invoices.</summary>
public sealed record SalesOrderAdvanceView(
    Guid PaymentId,
    string PaymentCode,
    decimal Amount,
    CustomerPaymentMethod Method,
    int StatusId,
    string Status,
    DateTimeOffset PaidAt,
    string? BankReference,
    AccountRef? CollectedBy,
    decimal AllocatedToInvoices,
    bool Reserved);

/// <summary>An invoice made from the order (the history tab), cancelled ones included, oldest first.</summary>
public sealed record SalesOrderInvoiceView(Guid Id, string Code, DateTimeOffset InvoicedAt, decimal Total, int StatusId, string Status, AccountRef? CreatedBy = null);

/// <summary>A harvest plan (KHT) serving the order. A Zone Manager sees only the plans that were handed over.</summary>
public sealed record SalesOrderHarvestPlanView(Guid Id, string Code, int StatusId, string Status, DateTimeOffset PlannedAt);

public sealed record SalesOrderView(
    Guid Id,
    string Code,
    int StatusId,
    string Status,
    DateTimeOffset OrderedAt,
    DateTimeOffset? PickupAt,
    SalesOrderCustomerView? Customer,
    bool WalkInCustomer,
    SalesOrderSource Source,
    AccountRef ReceivedBy,
    AccountRef? CreatedBy,
    string? Note,
    decimal Subtotal,
    decimal Discount,
    bool DiscountDecidedByOwner,
    decimal Total,
    decimal InvoicedTotal,
    decimal CustomerPaid,
    decimal AdvanceLeft,
    bool AdvanceReserved,
    IReadOnlyList<SalesOrderLineView> Lines,
    IReadOnlyList<SalesOrderProgressView> Progress,
    IReadOnlyList<SalesOrderAdvanceView> Advances,
    IReadOnlyList<SalesOrderInvoiceView> Invoices,
    IReadOnlyList<SalesOrderHarvestPlanView> HarvestPlans,
    SalesOrderRequestView? PurchaseRequest,
    IReadOnlyList<SalesOrderAction> AllowedActions,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    bool CompletedManually,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    string RowVersion);

/// <summary>A row of the order list.</summary>
/// <param name="WaitingDays">Days left until the pickup date (0 on the day or after it) while the order is Draft or Delivering; null without a pickup date or once it is closed.</param>
/// <param name="InvoiceCodes">The invoices made from the order that are not cancelled, oldest first.</param>
public sealed record SalesOrderListItem(
    Guid Id,
    string Code,
    int StatusId,
    string Status,
    DateTimeOffset OrderedAt,
    DateTimeOffset? PickupAt,
    int? WaitingDays,
    Guid? CustomerId,
    string? CustomerCode,
    string? CustomerName,
    string? CustomerPhone,
    bool WalkInCustomer,
    SalesOrderSource Source,
    string? ReceivedBy,
    string? CreatedBy,
    decimal Total,
    decimal InvoicedTotal,
    decimal CustomerPaid,
    IReadOnlyList<string> InvoiceCodes,
    IReadOnlyList<SalesOrderProgressView> Progress,
    string RowVersion);

/// <summary>The order list and export query. The periods are on the business date of the order time and of the pickup time..</summary>
/// <param name="Search">Text in the order code, the customer code, name or phone number, or an invoice code.</param>
/// <param name="StatusIds">Only these statuses (repeat the parameter); every status when empty.</param>
/// <param name="CreatedById">The account that saved the order.</param>
/// <param name="ReceivedById">The account that took the order ("Người nhận đặt").</param>
/// <param name="SortBy">code (the default, newest first), orderedAt, pickupAt or total.</param>
/// <param name="SortDir">asc or desc; the default is desc.</param>
public sealed record SalesOrderListQuery(
    string? Search = null,
    IReadOnlyList<int>? StatusIds = null,
    DateOnly? From = null,
    DateOnly? To = null,
    DateOnly? PickupFrom = null,
    DateOnly? PickupTo = null,
    Guid? CustomerId = null,
    SalesOrderSource? Source = null,
    Guid? CreatedById = null,
    Guid? ReceivedById = null,
    string? SortBy = null,
    string? SortDir = null,
    int Page = 1,
    int PageSize = 20);

/// <param name="Total">Σ the totals of the matching orders that are not cancelled, not only of the page.</param>
/// <param name="Invoiced">Σ what was invoiced of them.</param>
/// <param name="CustomerPaid">Σ what the customers paid on them.</param>
public sealed record SalesOrderListSummary(decimal Total, decimal Invoiced, decimal CustomerPaid);

public sealed record SalesOrderPage(IReadOnlyList<SalesOrderListItem> Items, int Page, int PageSize, int Total, SalesOrderListSummary Summary);

public sealed record SalesOrderNextCode(string Code);

/// <summary>The form of a new order made from a purchase request: the customer, the product in kg and the quantity (the kg accepted when the request was accepted), the pickup time and the source App.</summary>
public sealed record SalesOrderRequestDraft(
    Guid PurchaseRequestId,
    SalesOrderCustomerView Customer,
    DateTimeOffset SentAt,
    int RequestStatusId,
    IReadOnlyList<SalesOrderDraftLine> Lines,
    DateTimeOffset PickupAt,
    SalesOrderSource Source,
    string? Note);

public sealed record SalesOrderDraftLine(Guid ProductId, string ProductCode, string ProductName, Guid UnitId, string UnitCode, string UnitName, decimal Quantity, decimal UnitPrice);

/// <summary>An advance saved on an order, with the VietQR code when it waits for its transfer, and the order as it is now.</summary>
public sealed record SalesOrderAdvanceSaved(SalesOrderView Order, SalesOrderAdvanceView Advance, CustomerPaymentQr? Qr);

/// <summary>The order saved, and the VietQR code of its advance when the advance waits for the transfer.</summary>
public sealed record SalesOrderSaved(SalesOrderView Order, string Message, string MessageCode, CustomerPaymentQr? AdvanceQr = null);

/// <summary>An order cancelled or finished: the order and what is left of its advance, now general credit of the customer.</summary>
public sealed record SalesOrderClosed(SalesOrderView Order, decimal CreditLeft);
