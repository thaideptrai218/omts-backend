using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Returns;

/// <summary>The Owner's decision for one invoice line (BR-CLAIM-03): every line, once.</summary>
public sealed record SalesReturnLineEntry(Guid? InvoiceLineId, SalesReturnOutcome? Outcome);

/// <summary>The answer to MSG-HARVEST-02 for a lot of oysters thrown away that is still for sale (CLAIM-03.1, HARVEST-02.1).</summary>
/// <param name="StillForSale">Yes keeps the lot for sale; No closes it now with the fields below (BR-HARVEST-04).</param>
/// <param name="LeftoverKg">Only the oysters still unsold, never the discarded kg.</param>
/// <param name="CloseReason"><c>Unsold</c> or <c>Spoiled</c> when the leftover kg is above 0.</param>
public sealed record ThrownAwayLotAnswer(Guid HarvestLotId, bool StillForSale, decimal? LeftoverKg = null, string? CloseReason = null);

/// <summary>CLAIM-03: the whole invoice taken back. Questions are 409 answered by the field in <c>confirmationField</c>.</summary>
/// <param name="CustomerId">Only for a walk-in invoice: the profile that the invoice and its payments are attached to (CLAIM-03.2).</param>
/// <param name="OwnerAgreed">The answer Yes to MSG-CLAIM-04; not asked of the Owner.</param>
/// <param name="ThrownAwayLots">The answers to MSG-HARVEST-02.</param>
public sealed record CreateSalesReturnRequest(
    string? Reason, IReadOnlyList<SalesReturnLineEntry?>? Lines, string? RowVersion, Guid? CustomerId = null, bool OwnerAgreed = false, IReadOnlyList<ThrownAwayLotAnswer?>? ThrownAwayLots = null);

/// <summary>CLAIM-03.3, Owner only.</summary>
/// <param name="ConfirmNegativeRemaining">The answer Yes to MSG-CLAIM-06.</param>
public sealed record CancelSalesReturnRequest(string? Reason, string? RowVersion, bool ConfirmNegativeRemaining = false);

public sealed record SalesReturnLineView(
    Guid InvoiceLineId, int? LineNumber, string? ProductName, Guid HarvestLotId, string LotCode, decimal ReturnedKg, SalesReturnOutcome Outcome, decimal DiscardedKg, string? ProductCode = null);

/// <summary>The customer of the invoice of a return.</summary>
public sealed record SalesReturnCustomerRef(Guid Id, string Code, string Name);

/// <summary>The remaining kg of a lot the return put oysters back into: what CLAIM-03.3 step 2 shows.</summary>
/// <param name="RemainingKg">Now, 0 or more (BR-HARVEST-07).</param>
/// <param name="RemainingAfterCancelKg">After the cancellation: below zero when the oysters have been sold since (MSG-CLAIM-06).</param>
public sealed record SalesReturnLotFigures(Guid HarvestLotId, string LotCode, decimal RemainingKg, decimal RemainingAfterCancelKg);

/// <param name="Value">The total of the invoice when the return was recorded (BR-CLAIM-03).</param>
/// <param name="SalesOrder">The order the invoice was made from; null for an invoice without one.</param>
/// <param name="Customer">The customer of the invoice (the profile chosen when it was a walk-in invoice).</param>
/// <param name="ZoneCode">The zone of the invoice.</param>
/// <param name="BackForSaleKg">The kg of the lines taken back for sale.</param>
/// <param name="DiscardedKg">The kg of the lines thrown away.</param>
/// <param name="CanCancel">The caller may cancel the return now: it stands and the caller is the Cooperative Owner (CLAIM-03.3).</param>
public sealed record SalesReturnView(
    Guid Id, string Code, Guid InvoiceId, string InvoiceCode, DateOnly ReturnDate, string Reason, decimal Value, int StatusId, string Status, AccountRef RecordedBy,
    DateTimeOffset? CancelledAt, string? CancelReason, IReadOnlyList<SalesReturnLineView> Lines, IReadOnlyList<SalesReturnLotFigures> Lots, string RowVersion, string? Message = null, string? MessageCode = null,
    SalesInvoiceOrderRef? SalesOrder = null, SalesReturnCustomerRef? Customer = null, string ZoneCode = "", decimal BackForSaleKg = 0m, decimal DiscardedKg = 0m, bool CanCancel = false);

/// <summary>The list query of the sales returns. The period is on the return date; <c>Search</c> is text in the code of the return, of the invoice or of the customer, or in the name of the customer.</summary>
/// <param name="Sort"><c>code</c> (the default), <c>returnDate</c> or <c>value</c>.</param>
/// <param name="SortDir"><c>asc</c> or <c>desc</c> (the default).</param>
public sealed record SalesReturnListQuery(
    DateOnly? From = null, DateOnly? To = null, Guid? CustomerId = null, Guid? ZoneId = null, int? StatusId = null, string? Search = null, int Page = 1, int PageSize = 20, string? Sort = null,
    string? SortDir = null);

/// <summary>A return of the list.</summary>
/// <param name="RecordedAt">When the return was saved.</param>
/// <param name="BackForSaleKg">The kg of the lines taken back for sale.</param>
/// <param name="DiscardedKg">The kg of the lines thrown away.</param>
public sealed record SalesReturnListItem(
    Guid Id, string Code, DateOnly ReturnDate, DateTimeOffset? RecordedAt, Guid InvoiceId, string InvoiceCode, SalesInvoiceOrderRef? SalesOrder, SalesReturnCustomerRef? Customer, string ZoneCode,
    decimal Value, int StatusId, string Status, string? RecordedByName, int LineCount, decimal BackForSaleKg, decimal DiscardedKg);

/// <summary>The sums of the returns that stand among every match of the list, not only the page.</summary>
public sealed record SalesReturnListTotals(decimal Value, decimal BackForSaleKg, decimal DiscardedKg);

public sealed record SalesReturnListPage(IReadOnlyList<SalesReturnListItem> Items, int Total, int Page, int PageSize, SalesReturnListTotals Totals);

/// <param name="DebtAfter">The customer debt after the return; below 0 it is money owed back (DEBT-02).</param>
/// <param name="OwedBack">The money owed back, 0 when none.</param>
public sealed record SalesReturnSaved(SalesReturnView Return, decimal DebtAfter, decimal OwedBack, string Message, string MessageCode);

public sealed record SalesReturnPreviewLine(Guid InvoiceLineId, int LineNumber, string ProductName, Guid HarvestLotId, string LotCode, int LotStatusId, string LotStatus, decimal Kg, decimal UnitPrice, decimal Amount);

public sealed record SalesReturnPreviewClaim(Guid Id, string Code, DateTimeOffset SentAt, int StatusId, string Status, QualityClaimOffer? Offer);

/// <summary>What the Sales return screen shows before the save (CLAIM-03 step 1, SCR-CLAIM-03).</summary>
/// <param name="RequiresCustomer">A walk-in invoice: a customer profile must be chosen (CLAIM-03.2).</param>
/// <param name="Customer">Null for a walk-in invoice.</param>
/// <param name="CustomerDebt">Null until a profile is chosen.</param>
/// <param name="DebtAfter">The debt after the return; null until a profile is chosen.</param>
/// <param name="OwnerMustAgree">True for a Zone Manager: MSG-CLAIM-04 is asked.</param>
public sealed record SalesReturnPreview(
    Guid InvoiceId, string InvoiceCode, string RowVersion, bool RequiresCustomer, SalesInvoiceCustomerView? Customer, IReadOnlyList<SalesReturnPreviewLine> Lines, decimal Value,
    IReadOnlyList<SalesReturnPreviewClaim> QualityClaims, decimal? CustomerDebt, decimal? DebtAfter, decimal OwedBackAfter, bool OwnerMustAgree);
