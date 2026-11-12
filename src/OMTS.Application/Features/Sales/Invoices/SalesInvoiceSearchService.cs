using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>The matches of one search: the rows asked for, and every match in the figures.</summary>
internal sealed record SalesInvoiceMatches(DateOnly From, DateOnly To, int Count, decimal CompletedTotal, IReadOnlyList<SalesInvoiceRow> Rows);

/// <summary>
/// SALE-09 steps 1-3: the invoices of the zones the caller may view (BR-SALE-08, BR-FARM-08), today by default, with the total of the Completed ones. Read only; the API checks the
/// role, this the zones. A fixed number of commands, whatever the number of invoices: the allocation of the customers (BR-DEBT-05) is read for the page only, or for every match when
/// the paid filter needs it.
/// </summary>
public sealed class SalesInvoiceSearchService(
    ISalesInvoiceSearchReader queries, CustomerDebtReader debts, ZoneAccessGuard zoneAccess, ICurrentUser currentUser, IStatusCatalog statusCatalog, TimeProvider timeProvider)
{
    private const int MaxPageSize = 100;

    public async Task<Result<SalesInvoiceSearchResult>> SearchAsync(SalesInvoiceSearchQuery query, CancellationToken cancellationToken)
    {
        // The page number is capped so the offset cannot overflow.
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        var found = await FindAsync(query, PermissionAction.View, page, pageSize, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        var matches = found.Value!;
        // SALE-09.0.E2: the list always has a period, so an empty one is MSG-GEN-10.
        var empty = matches.Count == 0 ? UserMessages.NoDataForFilters("period and filters") : null;
        return new SalesInvoiceSearchResult(
            new Paged<SalesInvoiceRow>(matches.Rows, page, pageSize, matches.Count), matches.CompletedTotal, matches.From, matches.To, empty?.Text, empty?.Code);
    }

    /// <summary>SALE-09.2: every match, in the zones where the caller may export.</summary>
    internal Task<Result<SalesInvoiceMatches>> ExportRowsAsync(SalesInvoiceSearchQuery query, CancellationToken cancellationToken) =>
        FindAsync(query, PermissionAction.Export, 1, int.MaxValue, cancellationToken);

    private async Task<Result<SalesInvoiceMatches>> FindAsync(
        SalesInvoiceSearchQuery query, PermissionAction action, int page, int pageSize, CancellationToken cancellationToken)
    {
        var zones = await zoneAccess.PermittedZonesAsync(PermissionFunctions.SalesInvoices, action, cancellationToken);
        if (zones.IsEmpty || query.ZoneId is { } chosen && !zones.Allows(chosen))
        {
            return AppError.Forbidden;
        }

        var period = SalesInvoicePeriod.Resolve(query.From, query.To, timeProvider.Today());
        if (!period.IsSuccess)
        {
            return period.Error!;
        }

        var (from, to) = period.Value;
        IReadOnlyCollection<Guid>? zoneIds = query.ZoneId is { } zoneId ? [zoneId] : zones.AllZones ? null : zones.ZoneIds.ToList();
        var collector = query.CashCollectedByMe ? currentUser.AccountId : null;
        var keys = await queries.MatchingAsync(
            new SalesInvoiceSearchFilter(from, to, zoneIds, query.CustomerId, query.HarvestLotId, query.RaftId, query.StatusId, query.Code, collector, query.SalesOrderId), cancellationToken);

        IReadOnlyDictionary<Guid, CustomerDebtAllocation> allocations = new Dictionary<Guid, CustomerDebtAllocation>();
        if (query.Paid is { } paid)
        {
            allocations = await AllocationsAsync(keys, cancellationToken);
            keys = keys.Where(k => StateOf(k, allocations).IsPaid == paid).ToList();
        }

        var window = keys.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        if (query.Paid is null)
        {
            allocations = await AllocationsAsync(window, cancellationToken);
        }

        var rows = await RowsAsync(window, allocations, collector, cancellationToken);
        return new SalesInvoiceMatches(from, to, keys.Count, keys.Where(k => k.StatusId == StatusIds.SalesInvoice.Completed).Sum(k => k.Total), rows);
    }

    private async Task<IReadOnlyList<SalesInvoiceRow>> RowsAsync(
        List<SalesInvoiceSearchKey> window, IReadOnlyDictionary<Guid, CustomerDebtAllocation> allocations, Guid? collector, CancellationToken cancellationToken)
    {
        var ids = window.Select(k => k.Id).ToList();
        var rows = (await queries.RowsAsync(ids, cancellationToken)).ToDictionary(r => r.Id);
        var sendings = await queries.LatestSendingsAsync(ids, cancellationToken);
        var cash = collector is { } account ? await queries.CashAwaitingConfirmationAsync(ids, account, cancellationToken) : null;
        var now = timeProvider.GetUtcNow();
        return window.Select(key =>
        {
            var row = rows[key.Id];
            var state = StateOf(key, allocations);
            var delivery = sendings.TryGetValue(key.Id, out var sending)
                ? TraderMessageService.EffectiveStatus(sending.DeliveryStatusId, sending.DeliveryStatusAt, now)
                : StatusIds.MessageDelivery.NotSent;
            return new SalesInvoiceRow(
                row.Id, row.Code, row.InvoicedAt, row.InvoiceDate, new SalesInvoiceZoneView(row.ZoneId, row.ZoneCode, row.ZoneName),
                row.CustomerId is { } customerId ? new SalesInvoiceCustomerView(customerId, row.CustomerName!, row.CustomerPhone, row.CustomerCode) : null,
                row.CustomerId is null, row.Total, row.StatusId, statusCatalog.DisplayNameOf(row.StatusId), row.PaymentTerm, state.IsPaid, state.Unpaid, delivery,
                statusCatalog.DisplayNameOf(delivery), cash?.GetValueOrDefault(key.Id), row.SalesOrderId is { } orderId ? new SalesInvoiceOrderRef(orderId, row.SalesOrderCode ?? string.Empty) : null);
        }).ToList();
    }

    private Task<IReadOnlyDictionary<Guid, CustomerDebtAllocation>> AllocationsAsync(IEnumerable<SalesInvoiceSearchKey> keys, CancellationToken cancellationToken) =>
        debts.AllocationsAsync(keys.Where(k => k.CustomerId is not null).Select(k => k.CustomerId!.Value).ToList(), cancellationToken);

    private static SalesInvoicePaidState StateOf(SalesInvoiceSearchKey key, IReadOnlyDictionary<Guid, CustomerDebtAllocation> allocations) =>
        SalesInvoicePaidState.Of(
            key.Id, key.StatusId, key.Total, key.CustomerId, key.OwnPayments, key.CustomerId is { } customerId ? allocations.GetValueOrDefault(customerId) : null);
}
