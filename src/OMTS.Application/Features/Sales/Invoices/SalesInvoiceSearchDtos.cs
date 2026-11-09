using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>The filters of SALE-09 steps 2-3 (SCR-SALE-05).</summary>
/// <param name="From">Vietnam date, included; with <paramref name="To"/>, today when neither is given.</param>
/// <param name="HarvestLotId">Invoices with a line of this lot.</param>
/// <param name="RaftId">Invoices with a line of a lot of this raft.</param>
/// <param name="Paid">True: paid; false: not fully paid. A Cancelled invoice has no payment state, so it matches neither.</param>
/// <param name="Code">Part of the invoice code, read as plain text.</param>
/// <param name="CashCollectedByMe">"Cash I collected that is not confirmed yet" (SALE-09 step 3).</param>
public sealed record SalesInvoiceSearchQuery(
    DateOnly? From = null, DateOnly? To = null, Guid? ZoneId = null, Guid? CustomerId = null, Guid? HarvestLotId = null, Guid? RaftId = null,
    int? StatusId = null, bool? Paid = null, string? Code = null, bool CashCollectedByMe = false, int Page = 1, int PageSize = 20);

/// <summary>One row of the list (SALE-09 step 1).</summary>
/// <param name="Customer">Null for a walk-in customer.</param>
/// <param name="IsPaid">Null for a Cancelled invoice (<see cref="SalesInvoicePaidState"/>).</param>
/// <param name="UnpaidAmount">What is still to pay; 0 for a Cancelled invoice.</param>
/// <param name="DeliveryStatusId">The status of the latest sending, Not sent when there is none (BR-SALE-09).</param>
/// <param name="CashAwaitingConfirmation">Only with the cash filter: the cash of this invoice the caller collected that is Awaiting confirmation.</param>
public sealed record SalesInvoiceRow(
    Guid Id, string Code, DateTimeOffset InvoicedAt, DateOnly InvoiceDate, SalesInvoiceZoneView Zone, SalesInvoiceCustomerView? Customer, bool WalkInCustomer, decimal Total,
    int StatusId, string Status, SalesPaymentTerm PaymentTerm, bool? IsPaid, decimal UnpaidAmount, int DeliveryStatusId, string DeliveryStatus, decimal? CashAwaitingConfirmation);

/// <param name="Page">The rows of the page, newest first; <c>Total</c> counts every match.</param>
/// <param name="CompletedTotal">The total of the Completed invoices among all the matches, not only this page.</param>
/// <param name="Message">MSG-GEN-10 when nothing matches.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record SalesInvoiceSearchResult(Paged<SalesInvoiceRow> Page, decimal CompletedTotal, DateOnly From, DateOnly To, string? Message, string? MessageCode);

/// <summary>A price cut of the invoice (BR-SALE-10 (3)).</summary>
public sealed record SalesInvoicePriceCutView(decimal Amount, DateOnly CutDate, string Reason);

/// <summary>What the invoice does to the customer debt (BR-DEBT-02, BR-DEBT-05).</summary>
/// <param name="IsPaid">Null for a Cancelled invoice.</param>
/// <param name="Covered">The part of the total the payments cover.</param>
/// <param name="Unpaid">The part still to pay.</param>
/// <param name="CountsInCustomerDebt">False for a walk-in invoice and a Cancelled one.</param>
/// <param name="CustomerDebt">The debt of the profile now, below 0 for money owed back; null for a walk-in invoice.</param>
public sealed record SalesInvoiceDebtEffect(bool? IsPaid, decimal Covered, decimal Unpaid, bool CountsInCustomerDebt, decimal? CustomerDebt);
