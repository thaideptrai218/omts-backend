using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>SALE-07: the correction of a Completed invoice.</summary>
/// <param name="Reason">Required (BR-SALE-10).</param>
/// <param name="RowVersion">The version read: a parallel correction or cancellation makes this one stale, 409 MSG06.</param>
/// <param name="Lines">Data entry error: every line the invoice will have (BR-SALE-10 (2)).</param>
/// <param name="NotSoldWholeLotIds">Data entry error: the lots whose "sold the whole lot" mark is cleared (BR-HARVEST-04).</param>
/// <param name="Discount">Data entry error; null keeps the current one.</param>
/// <param name="OtherCharges">Data entry error; null keeps the current ones.</param>
/// <param name="CustomerId">Data entry error: another profile (SALE-07.5); a walk-in invoice with money to give back gets one with either kind (SALE-07.2). Null keeps the current one.</param>
/// <param name="PriceCutAmount">Price cut after a claim: the amount off the total (BR-SALE-10 (3)).</param>
/// <param name="ConfirmOwedBack">The answer Yes to MSG-SALE-17.</param>
public sealed record CorrectSalesInvoiceRequest(
    SalesInvoiceCorrectionKind? Kind,
    string? Reason,
    string? RowVersion,
    IReadOnlyList<SalesInvoiceLineEntry?>? Lines = null,
    IReadOnlyList<Guid>? NotSoldWholeLotIds = null,
    decimal? Discount = null,
    decimal? OtherCharges = null,
    Guid? CustomerId = null,
    decimal? PriceCutAmount = null,
    bool ConfirmOwedBack = false);

/// <summary>The answer to MSG-SALE-23 for one cash payment Awaiting confirmation.</summary>
public sealed record CashGivenBackAnswer(Guid PaymentId, bool GivenBack);

/// <summary>SALE-08: the cancellation of a Completed invoice.</summary>
/// <param name="Reason">Required (BR-SALE-11).</param>
/// <param name="RowVersion">The version read: a parallel correction or cancellation makes this one stale, 409 MSG06.</param>
/// <param name="CashGivenBack">One answer per cash payment Awaiting confirmation (SALE-08.3).</param>
/// <param name="CustomerId">The profile of the buyer of a walk-in invoice when payments are kept (SALE-08.2).</param>
/// <param name="ConfirmOwedBack">The answer Yes to MSG-SALE-17.</param>
public sealed record CancelSalesInvoiceRequest(
    string? Reason, string? RowVersion, IReadOnlyList<CashGivenBackAnswer>? CashGivenBack = null, Guid? CustomerId = null, bool ConfirmOwedBack = false);

/// <summary>One correction of the invoice as the audit log keeps it (SALE-09.1 step 2, BR-DEBT-04).</summary>
public sealed record SalesInvoiceCorrectionView(SalesInvoiceCorrectionKind Kind, DateOnly Date, string? Reason, decimal TotalBefore, decimal TotalAfter);
