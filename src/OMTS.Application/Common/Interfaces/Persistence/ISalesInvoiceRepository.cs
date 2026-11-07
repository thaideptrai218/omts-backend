using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Entities.Sales;

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
}

/// <summary>The sums of the customer debt formula (BR-DEBT-02), read from the database each time.</summary>
public interface ICustomerDebtQuery
{
    /// <summary>The figures of each profile asked for; every id has an entry, <see cref="CustomerDebtFigures.None"/> when it has nothing.</summary>
    Task<IReadOnlyDictionary<Guid, CustomerDebtFigures>> FiguresAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);
}
