using System.Globalization;
using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Application.Features.Reports.SalesPerformance;

/// <summary>The groups and the total of a REPORT-01 selection, before the customer money is added.</summary>
public sealed record SalesPerformanceCalculation(IReadOnlyList<SalesPerformanceGroup> Groups, SalesPerformanceFigures Totals);

/// <summary>
/// BR-REPORT-01 as pure functions, so the figures are unit tested and one rule serves the report, its invoices and its file. Revenue is the total of the invoice (already lower by its
/// price cuts, as the daily revenue of FARM-06); price cuts are shown on their own; sales returns are the values that stand, counted in the period of their invoice; net revenue is revenue
/// minus sales returns; sold kg is invoiced kg minus returned kg (BR-HARVEST-07). Grouped by product, the rows of the products hold the line amounts, and the discounts, the other charges and
/// the price cuts of the invoices have rows of their own, each with its part of the sales return, so that the rows add up to the revenue and to the net revenue. The database adds the figures
/// up per slice (<see cref="SalesPerformanceAggregates"/>); this names, merges (the week and the month are cut from the days) and orders the slices.
/// </summary>
public static class SalesPerformanceCalculator
{
    public const string WalkInKey = "walk-in";
    public const string DiscountsKey = "discounts";
    public const string OtherChargesKey = "other-charges";
    public const string PriceCutsKey = "price-cuts";

    private const string WalkInLabel = "Walk-in customers";

    public static SalesPerformanceCalculation Build(SalesPerformanceAggregates aggregates, SalesPerformanceGrouping grouping)
    {
        var rows = new Dictionary<string, Sums>();

        Sums Row(string key, string label, int rank = 0)
        {
            if (!rows.TryGetValue(key, out var row))
            {
                rows[key] = row = new Sums(key, label, rank);
            }

            return row;
        }

        foreach (var slice in aggregates.Slices)
        {
            var (key, label) = KeyOf(slice, grouping);
            Row(key, label).Add(slice.Sums);
        }

        if (grouping == SalesPerformanceGrouping.Product)
        {
            // The sales return takes back the whole invoice (BR-CLAIM-03), so each part of the invoice is returned with it; a part that is 0 on every invoice has no row.
            AddPart(Row, DiscountsKey, "Discounts", 1, aggregates.Parts.Discounts);
            AddPart(Row, OtherChargesKey, "Other charges", 2, aggregates.Parts.OtherCharges);
            AddPart(Row, PriceCutsKey, "Price cuts", 3, aggregates.Parts.PriceCuts);
        }

        var groups = rows.Values
            .OrderBy(r => r.Rank)
            .ThenBy(r => (grouping is SalesPerformanceGrouping.Customer or SalesPerformanceGrouping.Product) ? -r.Revenue : 0m)
            .ThenBy(r => r.Label, StringComparer.Ordinal)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => new SalesPerformanceGroup(r.Key, r.Label, r.InvoiceCount, r.InvoicedKg, r.Revenue, r.PriceCuts, r.Returns, r.Revenue - r.Returns, r.SoldKg, null, null, null, null))
            .ToList();
        // Period buckets read in time order: their keys are dates.
        if (grouping is SalesPerformanceGrouping.Day or SalesPerformanceGrouping.Week or SalesPerformanceGrouping.Month)
        {
            groups = groups.OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        }

        var total = aggregates.Total;
        return new SalesPerformanceCalculation(
            groups, new SalesPerformanceFigures(total.InvoiceCount, total.InvoicedKg, total.Revenue, total.PriceCuts, total.Returns, total.Revenue - total.Returns, total.SoldKg, null, null, null, null));
    }

    /// <summary>
    /// What the invoices of the group a report row names have in common, for the database to read only them (REPORT-01.3); null when the key names no group of this grouping, which has
    /// no invoice. Keys are compared as the report writes them: an id as its lower case hyphenated text (any letter case is accepted), the walk-in name in any letter case, the names of the
    /// three product rows exactly, a period by the first day it starts on.
    /// </summary>
    public static SalesPerformanceGroupFilter? GroupOf(SalesPerformanceGrouping grouping, string groupKey)
    {
        switch (grouping)
        {
            case SalesPerformanceGrouping.Customer:
                if (string.Equals(groupKey, WalkInKey, StringComparison.OrdinalIgnoreCase))
                {
                    return new SalesPerformanceGroupFilter(SalesPerformanceGroupKind.WalkIn);
                }

                return Guid.TryParseExact(groupKey, "D", out var customerId) ? new SalesPerformanceGroupFilter(SalesPerformanceGroupKind.Customer, customerId) : null;
            case SalesPerformanceGrouping.Zone:
                return Guid.TryParseExact(groupKey, "D", out var zoneId) ? new SalesPerformanceGroupFilter(SalesPerformanceGroupKind.Zone, zoneId) : null;
            case SalesPerformanceGrouping.Product:
                return groupKey switch
                {
                    DiscountsKey => new SalesPerformanceGroupFilter(SalesPerformanceGroupKind.Discounts),
                    OtherChargesKey => new SalesPerformanceGroupFilter(SalesPerformanceGroupKind.OtherCharges),
                    PriceCutsKey => new SalesPerformanceGroupFilter(SalesPerformanceGroupKind.PriceCuts),
                    _ => Guid.TryParseExact(groupKey, "D", out var productId) ? new SalesPerformanceGroupFilter(SalesPerformanceGroupKind.Product, productId) : null,
                };
            default:
                var unit = UnitOf(grouping);
                if (!DateOnly.TryParseExact(groupKey, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                    || !string.Equals(ReportPeriodBuckets.Key(start), groupKey, StringComparison.Ordinal)
                    || ReportPeriodBuckets.StartOf(start, unit) != start)
                {
                    return null;
                }

                return new SalesPerformanceGroupFilter(SalesPerformanceGroupKind.DateRange, null, start, ReportPeriodBuckets.EndOf(start, unit));
        }
    }

    /// <summary>The invoices of a group as report rows, newest first (REPORT-01.3).</summary>
    public static IReadOnlyList<SalesPerformanceInvoiceRow> InvoiceRowsOf(IEnumerable<SalesPerformanceInvoice> invoices) =>
        invoices
            .OrderByDescending(i => i.Date).ThenByDescending(i => i.Code, StringComparer.Ordinal)
            .Select(i => new SalesPerformanceInvoiceRow(
                i.Id, i.Code, i.Date,
                i.CustomerId is { } customerId ? new SalesPerformanceInvoiceCustomer(customerId, i.CustomerName ?? string.Empty) : null,
                new SalesPerformanceInvoiceZone(i.ZoneId, i.ZoneCode, i.ZoneName), i.Total, i.ReturnValue, i.Total - i.ReturnValue))
            .ToList();

    private static ReportPeriodUnit UnitOf(SalesPerformanceGrouping grouping) =>
        grouping == SalesPerformanceGrouping.Day ? ReportPeriodUnit.Day : grouping == SalesPerformanceGrouping.Week ? ReportPeriodUnit.Week : ReportPeriodUnit.Month;

    private static (string Key, string Label) KeyOf(SalesPerformanceSlice slice, SalesPerformanceGrouping grouping)
    {
        switch (grouping)
        {
            case SalesPerformanceGrouping.Customer:
                // The invoices of walk-in customers have no profile and are one row of their own.
                return slice.Id is { } customerId ? (customerId.ToString(), slice.Label ?? string.Empty) : (WalkInKey, WalkInLabel);
            case SalesPerformanceGrouping.Zone:
            case SalesPerformanceGrouping.Product:
                return (slice.Id!.Value.ToString(), slice.Label ?? string.Empty);
            default:
                var unit = UnitOf(grouping);
                var start = ReportPeriodBuckets.StartOf(slice.Date!.Value, unit);
                return (ReportPeriodBuckets.Key(start), ReportPeriodBuckets.Label(start, unit));
        }
    }

    private static void AddPart(Func<string, string, int, Sums> row, string key, string label, int rank, SalesPerformanceSums part)
    {
        if (part.InvoiceCount > 0)
        {
            row(key, label, rank).Add(part);
        }
    }

    private sealed class Sums(string key, string label, int rank)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
        public int Rank { get; } = rank;
        public int InvoiceCount { get; private set; }
        public decimal InvoicedKg { get; private set; }
        public decimal Revenue { get; private set; }
        public decimal PriceCuts { get; private set; }
        public decimal Returns { get; private set; }
        public decimal SoldKg { get; private set; }

        public void Add(SalesPerformanceSums sums)
        {
            InvoiceCount += sums.InvoiceCount;
            (InvoicedKg, Revenue, PriceCuts, Returns, SoldKg) = (InvoicedKg + sums.InvoicedKg, Revenue + sums.Revenue, PriceCuts + sums.PriceCuts, Returns + sums.Returns, SoldKg + sums.SoldKg);
        }
    }
}
