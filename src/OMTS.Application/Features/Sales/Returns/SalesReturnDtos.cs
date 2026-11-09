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

public sealed record SalesReturnLineView(Guid InvoiceLineId, int? LineNumber, string? ProductName, Guid HarvestLotId, string LotCode, decimal ReturnedKg, SalesReturnOutcome Outcome, decimal DiscardedKg);

/// <summary>The remaining kg of a lot the return put oysters back into: what CLAIM-03.3 step 2 shows.</summary>
/// <param name="RemainingKg">Now, 0 or more (BR-HARVEST-07).</param>
/// <param name="RemainingAfterCancelKg">After the cancellation: below zero when the oysters have been sold since (MSG-CLAIM-06).</param>
public sealed record SalesReturnLotFigures(Guid HarvestLotId, string LotCode, decimal RemainingKg, decimal RemainingAfterCancelKg);

/// <param name="Value">The total of the invoice when the return was recorded (BR-CLAIM-03).</param>
public sealed record SalesReturnView(
    Guid Id, string Code, Guid InvoiceId, string InvoiceCode, DateOnly ReturnDate, string Reason, decimal Value, int StatusId, string Status, AccountRef RecordedBy,
    DateTimeOffset? CancelledAt, string? CancelReason, IReadOnlyList<SalesReturnLineView> Lines, IReadOnlyList<SalesReturnLotFigures> Lots, string RowVersion);

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
