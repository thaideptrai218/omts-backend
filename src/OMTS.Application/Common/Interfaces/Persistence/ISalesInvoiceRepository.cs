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

    /// <summary>The invoice with its zone, customer, lines (product and lot) and payments, read only.</summary>
    Task<SalesInvoice?> GetAsync(Guid invoiceId, CancellationToken cancellationToken);

    /// <summary>The invoice with its customer, lines and payments, tracked: what sending it (SALE-06) and the end of the sale change and read.</summary>
    Task<SalesInvoice?> GetForSendingAsync(Guid invoiceId, CancellationToken cancellationToken);

    /// <summary>The sendings of the invoice message (BR-SALE-09), oldest first.</summary>
    Task<IReadOnlyList<SalesInvoiceSending>> ListSendingsAsync(Guid invoiceId, CancellationToken cancellationToken);
}

/// <summary>One sending of an invoice to the trader's phone, as its Notification row keeps it.</summary>
public sealed record SalesInvoiceSending(NotificationChannel Channel, string? PhoneNumber, int? DeliveryStatusId, DateTimeOffset? DeliveryStatusAt, DateTimeOffset CreatedAt);

/// <summary>The sums of the customer debt formula (BR-DEBT-02), read from the database each time.</summary>
public interface ICustomerDebtQuery
{
    /// <summary>The figures of each profile asked for; every id has an entry, <see cref="CustomerDebtFigures.None"/> when it has nothing.</summary>
    Task<IReadOnlyDictionary<Guid, CustomerDebtFigures>> FiguresAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);
}
