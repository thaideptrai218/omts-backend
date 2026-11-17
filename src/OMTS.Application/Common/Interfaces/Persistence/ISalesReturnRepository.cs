using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Filters of the sales return list; null means "any". The period is on the return date. A caller who reaches only some zones passes them in <see cref="ZoneIds"/> (null: every zone).</summary>
/// <param name="Search">Text in the code of the return, of its invoice or of its customer, or in the name of the customer.</param>
public sealed record SalesReturnFilter(DateOnly? From, DateOnly? To, Guid? CustomerId, Guid? ZoneId, IReadOnlySet<Guid>? ZoneIds, int? StatusId, string? Search);

public enum SalesReturnSort
{
    Code,
    ReturnDate,
    Value,
}

/// <summary>A return of the list with the names the list shows, its kg split by outcome and its line count.</summary>
public sealed record SalesReturnListRow(
    Guid Id, string Code, DateOnly ReturnDate, DateTimeOffset RecordedAt, Guid InvoiceId, string InvoiceCode, Guid? SalesOrderId, string? SalesOrderCode, Guid? CustomerId, string? CustomerCode,
    string? CustomerName, Guid ZoneId, string ZoneCode, decimal Value, int StatusId, Guid RecordedByAccountId, int LineCount, decimal BackForSaleKg, decimal DiscardedKg);

/// <summary>The sums of the returns that stand among every match, not only the page.</summary>
public sealed record SalesReturnSums(decimal Value, decimal BackForSaleKg, decimal DiscardedKg);

/// <summary>A return named by the order it came from: what the order shows as a link.</summary>
public sealed record SalesReturnOfOrder(Guid Id, string Code, int StatusId);

public interface ISalesReturnRepository
{
    void Add(SalesReturn salesReturn);

    /// <summary>The return with its lines, tracked, with its invoice (customer and lines) and the lot of each line (raft and product lines): what the cancellation changes and checks (CLAIM-03.3).</summary>
    Task<SalesReturn?> GetForEditAsync(Guid returnId, CancellationToken cancellationToken);

    /// <summary>The return with its lines (lots) and invoice (lines with product, customer, zone and order), read only.</summary>
    Task<SalesReturn?> GetDetailAsync(Guid returnId, CancellationToken cancellationToken);

    /// <summary>One page of the returns the filters match, with the number of matches and the sums of the returns that stand among them. The code breaks every tie.</summary>
    Task<(IReadOnlyList<SalesReturnListRow> Rows, int Total, SalesReturnSums Totals)> SearchAsync(
        SalesReturnFilter filter, SalesReturnSort sort, bool descending, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The returns of the invoices made from each of these orders, cancelled returns included, by code.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<SalesReturnOfOrder>>> OfOrdersAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken);
}
