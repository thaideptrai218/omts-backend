using OMTS.Application.Common.Models.Sales;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ISalesInvoiceRepository
{
    /// <summary>Adds the invoice with the lines it holds.</summary>
    void Add(SalesInvoice invoice);

    void AddPayment(CustomerPayment payment);

    /// <summary>A line added by a correction (SALE-07).</summary>
    void AddLine(SalesInvoiceLine line);

    /// <summary>A line removed by a correction (SALE-07).</summary>
    void RemoveLine(SalesInvoiceLine line);

    /// <summary>A price cut entered by a correction (SALE-07.1).</summary>
    void AddPriceCut(SalesInvoicePriceCut priceCut);

    /// <summary>The invoice saved with this key, if any: a repeated save returns it (BR-SALE-08 has no draft, so a second tap must not sell twice).</summary>
    Task<Guid?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>The invoice with this code, cancelled ones included: what a transfer with its VietQR code pays (GFR-08). Null when no invoice was saved with it.</summary>
    Task<SalesInvoiceRef?> FindByCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>The invoice with its zone, customer, linked request, weighing (cages), lines (product and lot), price cuts, payments and sales returns, read only.</summary>
    Task<SalesInvoice?> GetAsync(Guid invoiceId, CancellationToken cancellationToken);

    /// <summary>One page of the invoices of a customer profile, newest first, and how many it has in all.</summary>
    Task<(IReadOnlyList<CustomerInvoiceRow> Rows, int Total)> ListOfCustomerAsync(Guid customerId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The cages of the weighings on Completed invoices of each request; a request with none has no entry.</summary>
    Task<IReadOnlyDictionary<Guid, int>> DeliveredCagesAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken);

    /// <summary>The invoice with its customer, lines, payments and sales returns, tracked: what sending it (SALE-06), the end of the sale, a correction (SALE-07), a cancellation (SALE-08) and a sales return (CLAIM-03) change and read.</summary>
    Task<SalesInvoice?> GetForSendingAsync(Guid invoiceId, CancellationToken cancellationToken);

    /// <summary>The sendings of the invoice message (BR-SALE-09), oldest first.</summary>
    Task<IReadOnlyList<SalesInvoiceSending>> ListSendingsAsync(Guid invoiceId, CancellationToken cancellationToken);

    /// <summary>The correction events of the invoices in the audit log (BR-SALE-10, BR-DEBT-04), oldest first, one query whatever the number of invoices.</summary>
    Task<IReadOnlyList<SalesInvoiceCorrectionEvent>> ListCorrectionEventsAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken);
}

/// <summary>A saved invoice as a transfer names it: its customer profile (none for a walk-in customer).</summary>
public sealed record SalesInvoiceRef(Guid Id, string Code, Guid? CustomerId);

/// <summary>One correction of an invoice as its audit event keeps it: <see cref="After"/> is the JSON written by the correction.</summary>
public sealed record SalesInvoiceCorrectionEvent(Guid InvoiceId, DateTimeOffset OccurredAt, string? Reason, string? After);

/// <summary>One sending of an invoice to the trader's phone, as its Notification row keeps it.</summary>
public sealed record SalesInvoiceSending(NotificationChannel Channel, string? PhoneNumber, int? DeliveryStatusId, DateTimeOffset? DeliveryStatusAt, DateTimeOffset CreatedAt);

/// <summary>A customer profile on the debt screens (DEBT-03), merged ones included.</summary>
public sealed record DebtCustomerRow(Guid Id, string Name, string? PhoneNumber, string? Code = null);

/// <summary>Every row of the debt ledger of some profiles: their invoices, payments and debt adjustments, cancelled ones included.</summary>
public sealed record CustomerLedgerRows(
    IReadOnlyList<CustomerLedgerInvoice> Invoices, IReadOnlyList<CustomerLedgerPayment> Payments, IReadOnlyList<CustomerLedgerAdjustment> Adjustments);

/// <summary>The sums of the customer debt formula (BR-DEBT-02) and the rows of the allocation and the statement (BR-DEBT-04/05), read from the database each time.</summary>
public interface ICustomerDebtQuery
{
    /// <summary>The figures of each profile asked for; every id has an entry, <see cref="CustomerDebtFigures.None"/> when it has nothing.</summary>
    Task<IReadOnlyDictionary<Guid, CustomerDebtFigures>> FiguresAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary>The figures of every profile with an invoice or a payment (DEBT-03 step 1): one grouped sum per kind, whatever the number of profiles.</summary>
    Task<IReadOnlyDictionary<Guid, CustomerDebtFigures>> AllFiguresAsync(CancellationToken cancellationToken);

    /// <summary>Those of the profiles whose name or phone number contains <paramref name="search"/> (all of them when null).</summary>
    Task<IReadOnlyList<DebtCustomerRow>> CustomersAsync(IReadOnlyCollection<Guid> customerIds, string? search, CancellationToken cancellationToken);

    /// <summary>Every invoice, payment and debt adjustment of the profiles, cancelled ones included, in a fixed number of queries whatever the number of profiles.</summary>
    Task<CustomerLedgerRows> LedgerAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary>
    /// The rows of <see cref="LedgerAsync"/> for the staff statement of one profile (DEBT-03): the payments also carry their internal data (BR-DEBT-04), the names of the
    /// people read in the same query, so the statement sends the same commands whatever the number of payments.
    /// </summary>
    Task<CustomerLedgerRows> StatementLedgerAsync(Guid customerId, CancellationToken cancellationToken);

    /// <summary>The sales returns of the invoices of one profile, cancelled ones included (BR-DEBT-04); one query whatever the number of returns.</summary>
    Task<IReadOnlyList<CustomerLedgerReturn>> ReturnsOfAsync(Guid customerId, CancellationToken cancellationToken);

    /// <summary>The customer payments Awaiting confirmation, those without a customer included (SCR-DEBT-02 tab).</summary>
    Task<int> AwaitingConfirmationCountAsync(CancellationToken cancellationToken);
}
