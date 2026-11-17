using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Invoices;

namespace OMTS.Application.Features.Sales.Returns;

/// <summary>
/// The sales return list (all returns of the zones the caller can view, cancelled ones included): filtered, sorted and paged in the database, with the sums of the returns that stand among every
/// match. Viewing follows the return itself: SALES_RETURNS View in the zone of the invoice (Zone Manager of the zone, Owner). Read only.
/// </summary>
public sealed class SalesReturnListService(ISalesReturnRepository salesReturns, IAccountRepository accounts, ZoneAccessGuard zoneAccess, IStatusCatalog statusCatalog)
{
    private const int MaxPageSize = 100;

    public async Task<Result<SalesReturnListPage>> SearchAsync(SalesReturnListQuery query, CancellationToken cancellationToken)
    {
        var zones = await zoneAccess.PermittedZonesAsync(PermissionFunctions.SalesReturns, PermissionAction.View, cancellationToken);
        if (zones.IsEmpty || (query.ZoneId is { } chosen && !zones.Allows(chosen)))
        {
            return AppError.Forbidden;
        }

        if (query.From > query.To)
        {
            return AppError.Invalid("Period");
        }

        var sort = query.Sort?.Trim().ToLowerInvariant() switch
        {
            null or "" or "code" => SalesReturnSort.Code,
            "returndate" => SalesReturnSort.ReturnDate,
            "value" => SalesReturnSort.Value,
            _ => (SalesReturnSort?)null,
        };
        if (sort is null)
        {
            return AppError.Invalid("Sort", "Use code, returnDate or value.");
        }

        if (query.SortDir?.Trim().ToLowerInvariant() is not (null or "" or "asc" or "desc"))
        {
            return AppError.Invalid("Sort direction", "Use asc or desc.");
        }

        var descending = !string.Equals(query.SortDir?.Trim(), "asc", StringComparison.OrdinalIgnoreCase);
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        var filter = new SalesReturnFilter(query.From, query.To, query.CustomerId, query.ZoneId, zones.AllZones ? null : zones.ZoneIds.ToHashSet(), query.StatusId, query.Search);
        var (rows, total, sums) = await salesReturns.SearchAsync(filter, sort.Value, descending, page, pageSize, cancellationToken);
        var people = rows.Select(r => r.RecordedByAccountId).Distinct().ToList();
        var names = people.Count == 0 ? new Dictionary<Guid, string>() : await accounts.GetDisplayNamesAsync(people, cancellationToken);
        var items = rows.Select(r => new SalesReturnListItem(
            r.Id, r.Code, r.ReturnDate, r.RecordedAt, r.InvoiceId, r.InvoiceCode, r.SalesOrderId is { } orderId ? new SalesInvoiceOrderRef(orderId, r.SalesOrderCode ?? string.Empty) : null,
            r.CustomerId is { } customerId ? new SalesReturnCustomerRef(customerId, r.CustomerCode ?? string.Empty, r.CustomerName ?? string.Empty) : null, r.ZoneCode, r.Value, r.StatusId,
            statusCatalog.DisplayNameOf(r.StatusId), names.GetValueOrDefault(r.RecordedByAccountId), r.LineCount, r.BackForSaleKg, r.DiscardedKg)).ToList();
        return new SalesReturnListPage(items, total, page, pageSize, new SalesReturnListTotals(sums.Value, sums.BackForSaleKg, sums.DiscardedKg));
    }
}
