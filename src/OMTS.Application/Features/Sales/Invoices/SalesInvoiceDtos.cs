using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Traceability.Publishing;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>How the part of the total that the cash does not pay is paid (BR-SALE-07 (5)).</summary>
public enum SalesInvoiceRestPayment
{
    /// <summary>Nothing is left: the cash pays the whole total.</summary>
    None,

    /// <summary>The rest stays on the customer debt, with the agreement of the Cooperative Owner (SALE-05.8).</summary>
    PayLater,

    /// <summary>The rest is paid with the VietQR code of the invoice: the sale waits for the transfer (SALE-05 steps 7-8, SALE-05.7).</summary>
    VietQr,
}

/// <summary>One line as the seller enters it: the unit price is the price of the order line (the sale price of the unit without one) when left out.</summary>
/// <param name="Kg">The kg weighed, always required: the stock ledger counts kg. For a line in the kg unit it is also the quantity.</param>
/// <param name="PriceDecidedByOwner">The answer Yes to MSG-SALE-10 for a price other than the order price; not asked of the Owner.</param>
/// <param name="SalesOrderLineId">The line of the order this line delivers; null adds a line outside the order, which counts in no delivery.</param>
/// <param name="UnitId">The unit of the line and of its price; the kg unit of the product when left out.</param>
/// <param name="Quantity">The quantity in the unit; required for a unit other than kg.</param>
public sealed record SalesInvoiceLineEntry(
    Guid? ProductId, Guid? HarvestLotId, decimal? Kg, decimal? UnitPrice = null, bool PriceDecidedByOwner = false, Guid? SalesOrderLineId = null, Guid? UnitId = null, decimal? Quantity = null);

/// <summary>The payment taken at the port (SALE-05.6 to SALE-05.8).</summary>
/// <param name="CashAmount">The cash the trader hands over, 0 for none.</param>
/// <param name="Rest">How the part of the total that the cash does not pay is paid.</param>
/// <param name="SubtractExtraFromDebt">The answer to MSG-SALE-11 when cash above the total could lower the customer debt: null asks it.</param>
/// <param name="PayLaterOwnerAgreed">The answer Yes to MSG-SALE-12 (Owner's agreement outside OMTS); not needed with a linked request that agrees to it.</param>
public sealed record SalesInvoicePaymentEntry(decimal? CashAmount, SalesInvoiceRestPayment Rest = SalesInvoiceRestPayment.None, bool? SubtractExtraFromDebt = null, bool PayLaterOwnerAgreed = false);

/// <summary>
/// SALE-05: a new invoice, made from an order (DH). The customer is that of the order: it may be sent, and then must be the same (MSG-ORDER-06). The zone is the zone of the first lot of the invoice.
/// The request link of the old flow is not accepted for a new invoice; only a sale that was awaiting its transfer before invoices came from orders carries none of them.
/// </summary>
/// <param name="SalesOrderId">The order, Draft or Delivering; required for a new invoice.</param>
/// <param name="Code">1 to 30 of A-Z a-z 0-9 . _ -, unique regardless of letter case; empty takes the next HD code. At most 25 characters when the rest is paid with VietQR (the code is the transfer content).</param>
/// <param name="Note">At most 1000 characters.</param>
/// <param name="ZoneId">Ignored for an invoice made from an order (the zone of its first lot is taken).</param>
/// <param name="TakesRequestInFull">With a linked request; true when left out (BR-SALE-01).</param>
/// <param name="NotSoldWholeLotIds">The lots whose "sold the whole lot" mark the Zone Manager cleared: every other lot of the invoice is closed (BR-HARVEST-04).</param>
/// <param name="DiscountDecidedByOwner">The answer Yes to MSG-SALE-10 for the discount; not asked of the Owner.</param>
/// <param name="DeduplicationKey">The same key again returns the first invoice and creates nothing.</param>
public sealed record CreateSalesInvoiceRequest(
    Guid? CustomerId,
    bool WalkInCustomer,
    Guid? ZoneId,
    IReadOnlyList<SalesInvoiceLineEntry?>? Lines,
    SalesInvoicePaymentEntry? Payment,
    Guid? PurchaseRequestId = null,
    bool? TakesRequestInFull = null,
    IReadOnlyList<Guid>? NotSoldWholeLotIds = null,
    decimal? Discount = null,
    bool DiscountDecidedByOwner = false,
    decimal? OtherCharges = null,
    string? DeduplicationKey = null,
    Guid? SalesOrderId = null,
    string? Code = null,
    string? Note = null);

/// <summary>
/// PUT: the Owner (SALES_INVOICES Edit) changes a Completed invoice like a purchase receipt: the code, every line, the discount, the other charges and the note. The customer and the zone cannot change.
/// The debt moves by the difference of the totals; payments stay as they are.
/// </summary>
/// <param name="RowVersion">The version read: a parallel change makes this one stale, 409 MSG06.</param>
/// <param name="NotSoldWholeLotIds">The lots whose "sold the whole lot" mark is cleared (BR-HARVEST-04).</param>
/// <param name="ConfirmOwedBack">The answer Yes to MSG-SALE-17.</param>
public sealed record EditSalesInvoiceRequest(
    string? RowVersion,
    IReadOnlyList<SalesInvoiceLineEntry?>? Lines,
    string? Code = null,
    IReadOnlyList<Guid>? NotSoldWholeLotIds = null,
    decimal? Discount = null,
    bool DiscountDecidedByOwner = false,
    decimal? OtherCharges = null,
    string? Note = null,
    bool ConfirmOwedBack = false);

public sealed record SalesInvoiceZoneView(Guid Id, string Code, string Name);

public sealed record SalesInvoiceCustomerView(Guid Id, string Name, string? PhoneNumber, string? Code = null);

/// <param name="Unit">The unit of the line (the kg unit for a line of kg); <paramref name="Quantity"/> and <paramref name="UnitPrice"/> are in it.</param>
/// <param name="SalesOrderLineId">The order line the line delivers; null outside an order.</param>
public sealed record SalesInvoiceLineView(
    Guid Id, int LineNumber, Guid ProductId, string ProductName, Guid HarvestLotId, string LotCode, decimal Kg, decimal UnitPrice, decimal ListPrice, SalesPriceSource PriceSource,
    decimal Amount, bool SoldWholeLot, SalesInvoiceUnitView Unit, decimal Quantity, Guid? SalesOrderLineId);

public sealed record SalesInvoiceUnitView(Guid Id, string Code, string Name, decimal Factor);

/// <summary>The order an invoice was made from.</summary>
public sealed record SalesInvoiceOrderRef(Guid Id, string Code);

/// <summary>The order after the save of an invoice: its status now and whether it still has lines to deliver (the web then asks to finish the order and calls POST sales-orders/{id}/finish).</summary>
public sealed record SalesInvoiceOrderState(Guid Id, string Code, int StatusId, string Status, bool CanFinish);

public sealed record CustomerPaymentView(
    Guid Id, string Code, CustomerPaymentKind Kind, CustomerPaymentMethod Method, decimal Amount, DateOnly PaymentDate, DateTimeOffset CreatedAt, int StatusId, string Status, AccountRef? CollectedBy);

/// <summary>One sending of the invoice message (BR-SALE-09). A Sending row left by a server stop reads Not sent.</summary>
/// <param name="Channel">Sms, or ZaloOa once its templates are approved.</param>
public sealed record SalesInvoiceSendingView(string Channel, string? PhoneNumber, int DeliveryStatusId, string DeliveryStatus, DateTimeOffset? DeliveryStatusAt);

/// <param name="Customer">Null for a walk-in customer.</param>
/// <param name="PriceCutTotal">The price cuts of SALE-07, 0 until one is entered.</param>
/// <param name="QrUrl">The link of the QR code of the invoice (its provenance page), null when it has none.</param>
/// <param name="QrDecidedAt">When the end of the sale answered the QR question: null until then (POST finish).</param>
/// <param name="DeliveryStatusId">The status of the latest sending, Not sent when there is none (BR-SALE-09).</param>
/// <param name="PriceCuts">The price cuts of SALE-07, oldest first.</param>
/// <param name="DebtEffect">Whether it is paid and what it does to the customer debt (BR-DEBT-05).</param>
/// <param name="AllowedActions">The commands the caller is offered on this invoice now (SALE-09.1 step 3).</param>
/// <param name="LastCorrectionKind">The kind of the latest correction (SALE-07 POST-1); null until one is made.</param>
/// <param name="Corrections">Every correction from the audit log, oldest first (SALE-09.1 step 2).</param>
/// <param name="CancelledAt">Set by SALE-08.</param>
/// <param name="CancelReason">Set by SALE-08.</param>
/// <param name="SalesOrder">The order the invoice was made from; null for an invoice saved before invoices came from orders.</param>
/// <param name="Note">The seller's note.</param>
public sealed record SalesInvoiceView(
    Guid Id, string Code, DateTimeOffset InvoicedAt, DateOnly InvoiceDate, SalesInvoiceZoneView Zone, SalesInvoiceCustomerView? Customer, bool WalkInCustomer,
    Guid? PurchaseRequestId, bool TakesRequestInFull, IReadOnlyList<SalesInvoiceLineView> Lines, decimal TotalKg, decimal Subtotal, decimal Discount, bool DiscountDecidedByOwner,
    decimal OtherCharges, decimal PriceCutTotal, decimal Total, SalesPaymentTerm PaymentTerm, int StatusId, string Status, IReadOnlyList<CustomerPaymentView> Payments,
    AccountRef CreatedBy, string? QrToken, string? QrUrl, DateTimeOffset? QrDecidedAt, int DeliveryStatusId, string DeliveryStatus,
    IReadOnlyList<SalesInvoiceSendingView> Sendings, string RowVersion, IReadOnlyList<SalesInvoicePriceCutView> PriceCuts, SalesInvoiceDebtEffect DebtEffect,
    IReadOnlyList<SalesInvoiceAction> AllowedActions, SalesInvoiceCorrectionKind? LastCorrectionKind, DateTimeOffset? LastCorrectedAt, string? LastCorrectionReason,
    IReadOnlyList<SalesInvoiceCorrectionView> Corrections, DateTimeOffset? CancelledAt, string? CancelReason, SalesInvoiceOrderRef? SalesOrder = null, string? Note = null);

/// <summary>The end of the sale (SALE-05 steps 9-11).</summary>
/// <param name="WantsQrCode">The answer to MSG-SALE-07; null asks it (409 with <c>confirmationField</c>).</param>
public sealed record FinishSalesInvoiceRequest(bool? WantsQrCode);

/// <summary>The origin state of a lot of the invoice at the end of the sale: the provenance page lists the Published and Withdrawn ones (BR-ORIGIN-05).</summary>
/// <param name="Missing">What a lot that is Not published misses (BR-TRACE-01); empty for a lot that is ready or in another state.</param>
/// <param name="Message">MSG-TRACE-01 naming <paramref name="Missing"/>; null when nothing is missing.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record InvoiceLotOriginView(Guid HarvestLotId, string LotCode, HarvestLotOriginState OriginState, IReadOnlyList<OriginGap> Missing, string? Message, string? MessageCode)
{
    /// <summary>In lot code order.</summary>
    public static IReadOnlyList<InvoiceLotOriginView> Of(IEnumerable<InvoiceLotOrigin> lots) =>
        lots.OrderBy(l => l.LotCode, StringComparer.Ordinal).Select(View).ToList();

    private static InvoiceLotOriginView View(InvoiceLotOrigin lot)
    {
        var notReady = lot.Missing.Count == 0 ? null : UserMessages.ProvenanceNotReady(lot.LotCode, lot.Missing, atEndOfSale: true);
        return new InvoiceLotOriginView(lot.HarvestLotId, lot.LotCode, lot.State, lot.Missing, notReady?.Text, notReady?.Code);
    }
}

/// <summary>TRACE-01.0 steps 1-2 before the end of the sale: what a Yes to MSG-SALE-07 would publish, changing nothing.</summary>
/// <param name="Lots">Each lot of the invoice: a Not published lot with nothing missing is published by the Yes.</param>
/// <param name="GetsQrCode">A Yes now gives the invoice its QR code: a lot is Published or ready to publish.</param>
public sealed record InvoicePublishCheck(Guid InvoiceId, string InvoiceCode, IReadOnlyList<InvoiceLotOriginView> Lots, bool GetsQrCode);

/// <summary>The answer of the end of the sale and of Resend: the result of SALE-06 (Sent or Not sent) and the invoice as it now is.</summary>
/// <param name="Lots">At the end of the sale with a Yes to MSG-SALE-07: the origin state of each lot; otherwise empty.</param>
/// <param name="Message">MSG-SALE-16 when nothing could be sent (walk-in customer, no phone number); null otherwise.</param>
public sealed record SalesInvoiceSent(
    SalesInvoiceView Invoice, int DeliveryStatusId, string DeliveryStatus, string? QrUrl, IReadOnlyList<InvoiceLotOriginView> Lots, string? Message, string? MessageCode);

/// <summary>The answer of SALE-05: the invoice, the change to give back (SALE-05.6 step 2) and MSG-SALE-06.</summary>
/// <param name="SubtractedFromDebt">The cash above the total that lowered the customer debt (MSG-SALE-11).</param>
/// <param name="Order">The order after this save; null for an invoice with no order.</param>
public sealed record SalesInvoiceSaved(
    SalesInvoiceView Invoice, decimal Change, decimal SubtractedFromDebt, string Message, string MessageCode, SalesInvoiceOrderState? Order = null)
{
    /// <summary>The order is open and still has lines not fully delivered: the web asks whether to finish it.</summary>
    public bool OrderCanFinish => Order?.CanFinish == true;
}

/// <summary>The answer of POST: the saved invoice (201), or the sale awaiting the transfer of its VietQR code (202); exactly one is set.</summary>
public sealed record SalesInvoiceCreated(SalesInvoiceSaved? Saved, PendingSaleView? Pending);

/// <summary>A purchase request the invoice can be linked to (SALE-05.3): Accepted, not Fulfilled, pickup not more than 3 days ago.</summary>
public sealed record OpenPurchaseRequestView(Guid Id, Guid ProductId, string ProductName, decimal RequestedKg, decimal AcceptedKg, DateOnly PickupDate, bool PayLaterAgreed, string? ResponseNote);
