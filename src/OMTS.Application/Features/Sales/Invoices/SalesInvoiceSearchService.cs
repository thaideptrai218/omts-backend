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
/// role, this the zones. A fixed number of commands, whatever the number of invoices: the database counts, sums and pages the matches; what the allocation (BR-DEBT-05) leaves unpaid is
/// read for the customers of the page only, or of every match when the paid filter needs it.
/// </summary>
public sealed class SalesInvoiceSearchService(
    ISalesInvoiceSearchReader queries, CustomerDebtReader debts, ZoneAccessGuard zoneAccess, ICurrentUser currentUser, IStatusCatalog statusCatalog, TimeProvider timeProvider)
{
    private const int MaxPageSize = 100;

    /// <summary>The most invoices one list file holds.</summary>
    public const int MaxExportRows = 5_000;

    public async Task<Result<SalesInvoiceSearchResult>> SearchAsync(SalesInvoiceSearchQuery query, CancellationToken cancellationToken)
    {
        // The page number is capped so the offset cannot overflow.
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        var found = await FindAsync(query, PermissionAction.View, page, pageSize, null, cancellationToken);
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

    /// <summary>
    /// SALE-09.2: every match, in the zones where the caller may export, at most <see cref="MaxExportRows"/> (PM answer 12/11/2026, OMTS-224 [5.7]): a larger selection is 400 MSG-GEN-01
    /// naming <c>Filters</c>, and no more than one row above the limit is ever read. The limit counts the matches of the database filters, before the paid filter narrows them.
    /// </summary>
    internal Task<Result<SalesInvoiceMatches>> ExportRowsAsync(SalesInvoiceSearchQuery query, CancellationToken cancellationToken) =>
        FindAsync(query, PermissionAction.Export, 1, int.MaxValue, MaxExportRows, cancellationToken);

    private async Task<Result<SalesInvoiceMatches>> FindAsync(
        SalesInvoiceSearchQuery query, PermissionAction action, int page, int pageSize, int? maxMatches, CancellationToken cancellationToken)
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
        var filter = new SalesInvoiceSearchFilter(
            from, to, zoneIds, query.CustomerId, query.HarvestLotId, query.RaftId, query.StatusId, query.Code, collector, query.SalesOrderId, query.OrderCode);
        if (maxMatches is { } limit && await queries.CountAsync(filter, cancellationToken) > limit)
        {
            return new AppError(ErrorKind.Validation, UserMessages.ExportTooLarge("sales invoices", limit), new Dictionary<string, object?> { ["field"] = "Filters" });
        }

        // What the allocation leaves unpaid, of the invoices of the profiles in the matches: the paid filter needs it for every match, otherwise only the page does.
        IReadOnlyDictionary<Guid, decimal> unpaid = new Dictionary<Guid, decimal>();
        SalesInvoicePaidFilter? paidFilter = null;
        if (query.Paid is { } paid)
        {
            unpaid = await debts.UnpaidAmountsAsync(await queries.CustomerIdsAsync(filter, cancellationToken), cancellationToken);
            paidFilter = new SalesInvoicePaidFilter(paid, unpaid.Keys.ToList());
        }

        var matched = await queries.MatchAsync(filter, paidFilter, (page - 1) * pageSize, pageSize, cancellationToken);
        var window = matched.Keys.ToList();
        if (paidFilter is null)
        {
            unpaid = await debts.UnpaidAmountsAsync(window.Where(k => k.CustomerId is not null).Select(k => k.CustomerId!.Value).ToList(), cancellationToken);
        }

        var rows = await RowsAsync(window, unpaid, collector, cancellationToken);
        return new SalesInvoiceMatches(from, to, matched.Count, matched.CompletedTotal, rows);
    }

    private async Task<IReadOnlyList<SalesInvoiceRow>> RowsAsync(
        List<SalesInvoiceSearchKey> window, IReadOnlyDictionary<Guid, decimal> unpaid, Guid? collector, CancellationToken cancellationToken)
    {
        var ids = window.Select(k => k.Id).ToList();
        var rows = (await queries.RowsAsync(ids, cancellationToken)).ToDictionary(r => r.Id);
        var sendings = await queries.LatestSendingsAsync(ids, cancellationToken);
        var cash = collector is { } account ? await queries.CashAwaitingConfirmationAsync(ids, account, cancellationToken) : null;
        var now = timeProvider.GetUtcNow();
        return window.Select(key =>
        {
            var row = rows[key.Id];
            var state = StateOf(key, unpaid);
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

    private static SalesInvoicePaidState StateOf(SalesInvoiceSearchKey key, IReadOnlyDictionary<Guid, decimal> unpaid) =>
        SalesInvoicePaidState.OfUnpaid(key.StatusId, key.Total, key.CustomerId, key.OwnPayments, unpaid.GetValueOrDefault(key.Id));
}
