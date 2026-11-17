using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Filters of the order list; null means "any". The periods are business dates, both ends included.</summary>
public sealed record SalesOrderFilter(
    string? Search, IReadOnlyList<int>? StatusIds, DateOnly? From, DateOnly? To, DateOnly? PickupFrom, DateOnly? PickupTo, Guid? CustomerId,
    SalesOrderSource? Source, Guid? CreatedById, Guid? ReceivedById, SalesOrderSort Sort, bool Descending);

public enum SalesOrderSort
{
    Code,
    OrderedAt,
    PickupAt,
    Total,
}

/// <summary>An order of the list with what is worked out in SQL: the total of its invoices that are not cancelled, and the money the customer paid on it (advances that count).</summary>
/// <param name="InvoiceCodes">The codes of the invoices of the order that are not cancelled, oldest first.</param>
public sealed record SalesOrderRow(SalesOrder Order, decimal InvoicedTotal, decimal CustomerPaid, IReadOnlyList<string>? InvoiceCodes = null);

/// <summary>An invoice made from an order, cancelled ones included, for the history tab and the discount that was already shared.</summary>
public sealed record SalesOrderInvoiceRow(Guid Id, string Code, DateTimeOffset InvoicedAt, decimal Total, decimal DiscountAmount, int StatusId, Guid CreatedByAccountId);

/// <summary>The sums of the list over every match, the cancelled orders left out.</summary>
public sealed record SalesOrderTotals(decimal Total, decimal Invoiced, decimal CustomerPaid);

public interface ISalesOrderRepository
{
    /// <summary>The order with its customer, lines (and their products) and purchase request, tracked.</summary>
    Task<SalesOrder?> GetForEditAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>The order that has this code regardless of letter case, other than <paramref name="exceptOrderId"/>; cancelled orders included; untracked, header only.</summary>
    Task<SalesOrder?> FindByCodeAsync(string code, Guid? exceptOrderId, CancellationToken cancellationToken);

    /// <summary>The order made from the purchase request, untracked, header only.</summary>
    Task<SalesOrder?> FindByPurchaseRequestAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>The orders made from these purchase requests, untracked, header only.</summary>
    Task<IReadOnlyList<SalesOrder>> FindByPurchaseRequestsAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken);

    Task<SalesOrder?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken);

    Task<(IReadOnlyList<SalesOrderRow> Rows, int Total, SalesOrderTotals Totals)> SearchAsync(SalesOrderFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    Task<IReadOnlyList<SalesOrderRow>> ListAsync(SalesOrderFilter filter, CancellationToken cancellationToken);

    /// <summary>The customer payments that are advances on the order (every status), with their allocations; untracked, oldest first.</summary>
    Task<IReadOnlyList<CustomerPayment>> AdvancesOfAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>The advances of each of these orders, for a list; untracked.</summary>
    Task<IReadOnlyList<CustomerPayment>> AdvancesOfAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken);

    /// <summary>Whether an invoice that is not cancelled was made from the order (<see cref="OMTS.Application.Features.Sales.Orders.SalesOrderRules.BlocksCancel"/>).</summary>
    Task<bool> HasLiveInvoiceAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>The total of the invoices of the order that are not cancelled ("Đã lập HĐ").</summary>
    Task<decimal> InvoicedTotalAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>Every invoice made from the order, cancelled ones included, oldest first; untracked.</summary>
    Task<IReadOnlyList<SalesOrderInvoiceRow>> InvoicesOfAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>
    /// The quantity delivered on each line of these orders, in the unit of the line, by order line id: the sum over the lines of the invoices that are not cancelled; a line with no delivery is absent.
    /// <paramref name="exceptInvoiceId"/> leaves one invoice out, for the invoice that is being saved or edited.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, decimal>> DeliveredByLineAsync(IReadOnlyCollection<Guid> orderIds, Guid? exceptInvoiceId, CancellationToken cancellationToken);

    /// <summary>The advances of the order that count (Completed or Awaiting confirmation), with their allocations, tracked, oldest first: what a new invoice of the order sets money against.</summary>
    Task<IReadOnlyList<CustomerPayment>> CountedAdvancesForUpdateAsync(Guid orderId, CancellationToken cancellationToken);

    void Add(SalesOrder order);
    void AddLine(SalesOrderLine line);
    void RemoveLine(SalesOrderLine line);
}
