using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ISalesInvoiceRepository
{
    /// <summary>Adds the invoice with the lines it holds.</summary>
    void Add(SalesInvoice invoice);

    void AddPayment(CustomerPayment payment);

    /// <summary>The invoice saved with this key, if any: a repeated save returns it (BR-SALE-08 has no draft, so a second tap must not sell twice).</summary>
    Task<Guid?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>The invoice with this code, cancelled ones included: what a transfer with its VietQR code pays (GFR-08). Null when no invoice was saved with it.</summary>
    Task<SalesInvoiceRef?> FindByCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>The invoice with its zone, customer, lines (product and lot), price cuts and payments, read only.</summary>
    Task<SalesInvoice?> GetAsync(Guid invoiceId, CancellationToken cancellationToken);

    /// <summary>The invoice with its customer, lines and payments, tracked: what sending it (SALE-06) and the end of the sale change and read.</summary>
    Task<SalesInvoice?> GetForSendingAsync(Guid invoiceId, CancellationToken cancellationToken);

    /// <summary>The sendings of the invoice message (BR-SALE-09), oldest first.</summary>
    Task<IReadOnlyList<SalesInvoiceSending>> ListSendingsAsync(Guid invoiceId, CancellationToken cancellationToken);
}

/// <summary>A saved invoice as a transfer names it: its customer profile (none for a walk-in customer).</summary>
public sealed record SalesInvoiceRef(Guid Id, string Code, Guid? CustomerId);

/// <summary>One sending of an invoice to the trader's phone, as its Notification row keeps it.</summary>
public sealed record SalesInvoiceSending(NotificationChannel Channel, string? PhoneNumber, int? DeliveryStatusId, DateTimeOffset? DeliveryStatusAt, DateTimeOffset CreatedAt);

/// <summary>A customer profile on the debt screens (DEBT-03), merged ones included.</summary>
public sealed record DebtCustomerRow(Guid Id, string Name, string? PhoneNumber);

/// <summary>The sums of the customer debt formula (BR-DEBT-02) and the rows of the allocation and the statement (BR-DEBT-04/05), read from the database each time.</summary>
public interface ICustomerDebtQuery
{
    /// <summary>The figures of each profile asked for; every id has an entry, <see cref="CustomerDebtFigures.None"/> when it has nothing.</summary>
    Task<IReadOnlyDictionary<Guid, CustomerDebtFigures>> FiguresAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary>The figures of every profile with an invoice or a payment (DEBT-03 step 1): one grouped sum per kind, whatever the number of profiles.</summary>
    Task<IReadOnlyDictionary<Guid, CustomerDebtFigures>> AllFiguresAsync(CancellationToken cancellationToken);

    /// <summary>Those of the profiles whose name or phone number contains <paramref name="search"/> (all of them when null).</summary>
    Task<IReadOnlyList<DebtCustomerRow>> CustomersAsync(IReadOnlyCollection<Guid> customerIds, string? search, CancellationToken cancellationToken);

    /// <summary>Every invoice and payment of the profiles, cancelled ones included, in two queries whatever the number of profiles.</summary>
    Task<(IReadOnlyList<CustomerLedgerInvoice> Invoices, IReadOnlyList<CustomerLedgerPayment> Payments)> LedgerAsync(
        IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary>
    /// The rows of <see cref="LedgerAsync"/> for the staff statement of one profile (DEBT-03): the payments also carry their internal data (BR-DEBT-04), the names of the
    /// people read in the same query, so the statement sends the same commands whatever the number of payments.
    /// </summary>
    Task<(IReadOnlyList<CustomerLedgerInvoice> Invoices, IReadOnlyList<CustomerLedgerPayment> Payments)> StatementLedgerAsync(
        Guid customerId, CancellationToken cancellationToken);

    /// <summary>The customer payments Awaiting confirmation, those without a customer included (SCR-DEBT-02 tab).</summary>
    Task<int> AwaitingConfirmationCountAsync(CancellationToken cancellationToken);
}
