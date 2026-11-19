using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.CashBook;

namespace OMTS.Application.Features.Reports.EndOfDay;

/// <summary>
/// The end-of-day report (KiotViet "Cuối ngày"): the sales, the money and the goods of one business day, worked out each time and never stored. The API checks the right (SALES_REPORTS View, or
/// Export for the file: the Cooperative Owner and the Accountant, decision D5 of 19/11/2026). The zones limit the sales and the lots; the money is the cash book of the day (it has no zone), shown
/// only to a user of every zone. The money reuses the cash book query, so its sums are those of the cash book.
/// </summary>
public sealed class EndOfDayService(
    ReportFilterResolver filters, IEndOfDayReportQuery query, ICashBookQuery cashBook, ZoneAccessGuard zoneAccess, TimeProvider timeProvider)
{
    public const string MethodField = "Method";

    public Task<Result<EndOfDayReport>> ReportAsync(EndOfDayQuery request, CancellationToken cancellationToken) =>
        BuildAsync(request, PermissionAction.View, cancellationToken);

    /// <summary>Shared with the export, which asks for the Export right.</summary>
    internal async Task<Result<EndOfDayReport>> BuildAsync(EndOfDayQuery request, PermissionAction action, CancellationToken cancellationToken)
    {
        var computedAt = timeProvider.GetUtcNow();
        var date = request.Date ?? BusinessClock.ToBusinessDate(computedAt);
        var (fund, methodOk) = ParseMethod(request.Method);
        var filter = await filters.ResolveAsync(
            PermissionFunctions.SalesReports, action, date, date, request.ZoneIds, null, null, methodOk ? [] : [MethodField], cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var f = filter.Value!;
        var facts = await query.FactsAsync(date, f.ZoneIds, request.CreatedBy, cancellationToken);
        var sales = SalesOf(facts);
        var goods = GoodsOf(facts);
        var money = (await zoneAccess.PermittedZonesAsync(PermissionFunctions.SalesReports, action, cancellationToken)).AllZones
            ? MoneyOf(await cashBook.ListAsync(
                new CashBookQuery(fund, null, date, date, null, null, CashEntryStatus.Recorded, request.CreatedBy, null, null, null, null, CashBookSort.Time, false), cancellationToken))
            : MoneyOf([]);
        var empty = sales.Invoices.Count == 0 && sales.Returns.Count == 0 && money.Entries.Count == 0 && goods.Lots.Count == 0
            ? UserMessages.NoDataForFilters("date and filters")
            : null;
        return new EndOfDayReport(
            computedAt, date, new EndOfDayChoices(f.Zones, fund == CashFundFilter.All ? "all" : fund.ToString(), request.CreatedBy), sales, money, goods, empty?.Text, empty?.Code);
    }

    private static EndOfDaySales SalesOf(EndOfDayFacts facts)
    {
        var invoices = facts.Invoices
            .Select(i => new EndOfDayInvoiceRow(
                i.Id, i.Code, i.InvoicedAt, i.CustomerId is { } id ? new EndOfDayCustomer(id, i.CustomerName ?? string.Empty) : null, i.ZoneName, i.Kg, i.Total, i.PaidNow))
            .ToList();
        var returns = facts.Returns.Select(r => new EndOfDayReturnRow(r.Id, r.Code, r.InvoiceId, r.InvoiceCode, r.CustomerName, r.ZoneName, r.Value)).ToList();
        var revenue = invoices.Sum(i => i.Total);
        var returned = returns.Sum(r => r.Value);
        return new EndOfDaySales(invoices, returns, new EndOfDaySalesTotals(invoices.Count, invoices.Sum(i => i.Kg), revenue, invoices.Sum(i => i.PaidNow), returns.Count, returned, revenue - returned));
    }

    private static EndOfDayGoods GoodsOf(EndOfDayFacts facts)
    {
        var products = facts.Products.Select(p => new EndOfDayProductRow(p.ProductId, p.Code, p.Name, p.Kg)).ToList();
        var lots = facts.Lots.Select(l => new EndOfDayLotRow(l.Id, l.LotCode, l.ZoneName, l.WeighedKg, l.WeighedAt)).ToList();
        return new EndOfDayGoods(products, lots, new EndOfDayGoodsTotals(products.Sum(p => p.Kg), lots.Count, lots.Sum(l => l.WeighedKg)));
    }

    private static EndOfDayMoney MoneyOf(IReadOnlyList<CashBookEntry> lines)
    {
        var entries = lines.Select(e => new EndOfDayMoneyEntry(e.Source, e.Code, e.OccurredAt, e.Direction, e.Fund, e.PartyName, e.Amount, e.Note)).ToList();
        var byFund = new[] { "Cash", "Bank", "EWallet" }
            .Select(fund =>
            {
                var receipts = entries.Where(e => e.Fund == fund && e.Direction == "Receipt").Sum(e => e.Amount);
                var payments = entries.Where(e => e.Fund == fund && e.Direction == "Payment").Sum(e => e.Amount);
                return new EndOfDayFundTotals(fund, receipts, payments, receipts - payments);
            })
            .Where(t => t.Receipts != 0m || t.Payments != 0m)
            .ToList();
        var totalReceipts = byFund.Sum(t => t.Receipts);
        var totalPayments = byFund.Sum(t => t.Payments);
        return new EndOfDayMoney(entries, byFund, new EndOfDayMoneyTotals(totalReceipts, totalPayments, totalReceipts - totalPayments));
    }

    /// <summary>All funds when none or "all"; Cash, Bank or EWallet in any letter case; anything else is not ok.</summary>
    internal static (CashFundFilter Fund, bool Ok) ParseMethod(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" => (CashFundFilter.All, true),
        "cash" => (CashFundFilter.Cash, true),
        "bank" => (CashFundFilter.Bank, true),
        "ewallet" => (CashFundFilter.EWallet, true),
        _ => (CashFundFilter.All, false),
    };
}
