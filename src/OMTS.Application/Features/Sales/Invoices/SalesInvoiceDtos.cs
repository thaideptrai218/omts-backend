using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>How the part of the total that the cash does not pay is paid (BR-SALE-07 (5)). VietQR comes with the pending sale of PR 7.</summary>
public enum SalesInvoiceRestPayment
{
    /// <summary>Nothing is left: the cash pays the whole total.</summary>
    None,

    /// <summary>The rest stays on the customer debt, with the agreement of the Cooperative Owner (SALE-05.8).</summary>
    PayLater,
}

/// <summary>One line as the Zone Manager enters it: the unit price is the list price when left out.</summary>
/// <param name="PriceDecidedByOwner">The answer Yes to MSG-SALE-10 for a price other than the list price; not asked of the Owner.</param>
public sealed record SalesInvoiceLineEntry(Guid? ProductId, Guid? HarvestLotId, decimal? Kg, decimal? UnitPrice = null, bool PriceDecidedByOwner = false);

/// <summary>The payment taken at the port (SALE-05.6 to SALE-05.8).</summary>
/// <param name="CashAmount">The cash the trader hands over, 0 for none.</param>
/// <param name="Rest">How the part of the total that the cash does not pay is paid.</param>
/// <param name="SubtractExtraFromDebt">The answer to MSG-SALE-11 when cash above the total could lower the customer debt: null asks it.</param>
/// <param name="PayLaterOwnerAgreed">The answer Yes to MSG-SALE-12 (Owner's agreement outside OMTS); not needed with a linked request that agrees to it.</param>
public sealed record SalesInvoicePaymentEntry(decimal? CashAmount, SalesInvoiceRestPayment Rest = SalesInvoiceRestPayment.None, bool? SubtractExtraFromDebt = null, bool PayLaterOwnerAgreed = false);

/// <summary>SALE-05: a new invoice. A walk-in customer has no profile and must be chosen as such.</summary>
/// <param name="ZoneId">The zone the invoice counts in; the zone of the first lot when left out (BR-SALE-08).</param>
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
    string? DeduplicationKey = null);

public sealed record SalesInvoiceZoneView(Guid Id, string Code, string Name);

public sealed record SalesInvoiceCustomerView(Guid Id, string Name, string? PhoneNumber);

public sealed record SalesInvoiceLineView(
    Guid Id, int LineNumber, Guid ProductId, string ProductName, Guid HarvestLotId, string LotCode, decimal Kg, decimal UnitPrice, decimal ListPrice, SalesPriceSource PriceSource,
    decimal Amount, bool SoldWholeLot);

public sealed record CustomerPaymentView(
    Guid Id, string Code, CustomerPaymentKind Kind, CustomerPaymentMethod Method, decimal Amount, DateOnly PaymentDate, int StatusId, string Status, AccountRef? CollectedBy);

/// <param name="Customer">Null for a walk-in customer.</param>
/// <param name="PriceCutTotal">The price cuts of SALE-07, 0 until one is entered.</param>
public sealed record SalesInvoiceView(
    Guid Id, string Code, DateTimeOffset InvoicedAt, DateOnly InvoiceDate, SalesInvoiceZoneView Zone, SalesInvoiceCustomerView? Customer, bool WalkInCustomer,
    Guid? PurchaseRequestId, bool TakesRequestInFull, IReadOnlyList<SalesInvoiceLineView> Lines, decimal TotalKg, decimal Subtotal, decimal Discount, bool DiscountDecidedByOwner,
    decimal OtherCharges, decimal PriceCutTotal, decimal Total, SalesPaymentTerm PaymentTerm, int StatusId, string Status, IReadOnlyList<CustomerPaymentView> Payments,
    AccountRef CreatedBy, string RowVersion);

/// <summary>The answer of SALE-05: the invoice, the change to give back (SALE-05.6 step 2) and MSG-SALE-06.</summary>
/// <param name="SubtractedFromDebt">The cash above the total that lowered the customer debt (MSG-SALE-11).</param>
public sealed record SalesInvoiceSaved(SalesInvoiceView Invoice, decimal Change, decimal SubtractedFromDebt, string Message, string MessageCode);

/// <summary>A purchase request the invoice can be linked to (SALE-05.3): Accepted, not Fulfilled, pickup not more than 3 days ago.</summary>
public sealed record OpenPurchaseRequestView(Guid Id, Guid ProductId, string ProductName, decimal RequestedKg, decimal AcceptedKg, DateOnly PickupDate, bool PayLaterAgreed, string? ResponseNote);
