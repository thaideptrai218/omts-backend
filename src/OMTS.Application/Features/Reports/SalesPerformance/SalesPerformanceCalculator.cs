using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Application.Features.Reports.SalesPerformance;

/// <summary>The groups and the total of a REPORT-01 selection, before the customer money is added.</summary>
public sealed record SalesPerformanceCalculation(IReadOnlyList<SalesPerformanceGroup> Groups, SalesPerformanceFigures Totals);

/// <summary>
/// BR-REPORT-01 as pure functions, so the figures are unit tested and one rule serves the report, its invoices and its file. Revenue is the total of the invoice (already lower by its
/// price cuts, as the daily revenue of FARM-06); price cuts are shown on their own; sales returns are the values that stand, counted in the period of their invoice; net revenue is revenue
/// minus sales returns; sold kg is invoiced kg minus returned kg (BR-HARVEST-07). Grouped by product, the rows of the products hold the line amounts, and the discounts, the other charges and
/// the price cuts of the invoices have rows of their own, each with its part of the sales return, so that the rows add up to the revenue and to the net revenue.
/// </summary>
public static class SalesPerformanceCalculator
{
    public const string WalkInKey = "walk-in";
    public const string DiscountsKey = "discounts";
    public const string OtherChargesKey = "other-charges";
    public const string PriceCutsKey = "price-cuts";

    private const string WalkInLabel = "Walk-in customers";

    public static SalesPerformanceCalculation Build(SalesPerformanceFacts facts, SalesPerformanceGrouping grouping)
    {
        var linesOf = facts.Lines.ToLookup(l => l.InvoiceId);
        var total = new Sums("total", "Total", 0);
        var rows = new Dictionary<string, Sums>();

        Sums Row(string key, string label, int rank = 0)
        {
            if (!rows.TryGetValue(key, out var row))
            {
                rows[key] = row = new Sums(key, label, rank);
            }

            return row;
        }

        foreach (var invoice in facts.Invoices)
        {
            var lines = linesOf[invoice.Id].ToList();
            var kg = lines.Sum(l => l.Kg);
            var returnedKg = lines.Sum(l => l.ReturnedKg);
            total.Add(invoice.Id, kg, invoice.Total, invoice.PriceCuts, invoice.ReturnValue, kg - returnedKg);
            if (grouping != SalesPerformanceGrouping.Product)
            {
                var (key, label) = KeyOf(invoice, grouping);
                Row(key, label).Add(invoice.Id, kg, invoice.Total, invoice.PriceCuts, invoice.ReturnValue, kg - returnedKg);
                continue;
            }

            foreach (var product in lines.GroupBy(l => l.ProductId))
            {
                var amount = product.Sum(l => l.Amount);
                Row(product.Key.ToString(), product.First().ProductName)
                    .Add(invoice.Id, product.Sum(l => l.Kg), amount, 0m, invoice.HasReturn ? amount : 0m, product.Sum(l => l.Kg - l.ReturnedKg));
            }

            // The sales return takes back the whole invoice (BR-CLAIM-03), so each part of the invoice is returned with it.
            AddPart(Row(DiscountsKey, "Discounts", 1), invoice, -invoice.Discount, 0m);
            AddPart(Row(OtherChargesKey, "Other charges", 2), invoice, invoice.OtherCharges, 0m);
            AddPart(Row(PriceCutsKey, "Price cuts", 3), invoice, -invoice.PriceCuts, invoice.PriceCuts);
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

        return new SalesPerformanceCalculation(
            groups, new SalesPerformanceFigures(total.InvoiceCount, total.InvoicedKg, total.Revenue, total.PriceCuts, total.Returns, total.Revenue - total.Returns, total.SoldKg, null, null, null, null));
    }

    /// <summary>The invoices of the group a report row names, newest first (REPORT-01.3). A key that names no group of this grouping gives none.</summary>
    public static IReadOnlyList<SalesPerformanceInvoiceRow> InvoicesOf(SalesPerformanceFacts facts, SalesPerformanceGrouping grouping, string groupKey)
    {
        var linesOf = facts.Lines.ToLookup(l => l.InvoiceId);
        bool InGroup(SalesPerformanceInvoice invoice) => grouping switch
        {
            SalesPerformanceGrouping.Product => groupKey switch
            {
                DiscountsKey => invoice.Discount != 0m,
                OtherChargesKey => invoice.OtherCharges != 0m,
                PriceCutsKey => invoice.PriceCuts != 0m,
                _ => linesOf[invoice.Id].Any(l => string.Equals(l.ProductId.ToString(), groupKey, StringComparison.OrdinalIgnoreCase)),
            },
            _ => string.Equals(KeyOf(invoice, grouping).Key, groupKey, StringComparison.OrdinalIgnoreCase),
        };

        return facts.Invoices
            .Where(InGroup)
            .OrderByDescending(i => i.Date).ThenByDescending(i => i.Code, StringComparer.Ordinal)
            .Select(i => new SalesPerformanceInvoiceRow(
                i.Id, i.Code, i.Date,
                i.CustomerId is { } customerId ? new SalesPerformanceInvoiceCustomer(customerId, i.CustomerName ?? string.Empty) : null,
                new SalesPerformanceInvoiceZone(i.ZoneId, i.ZoneCode, i.ZoneName), i.Total, i.ReturnValue, i.Total - i.ReturnValue))
            .ToList();
    }

    private static (string Key, string Label) KeyOf(SalesPerformanceInvoice invoice, SalesPerformanceGrouping grouping)
    {
        switch (grouping)
        {
            case SalesPerformanceGrouping.Customer:
                // The invoices of walk-in customers have no profile and are one row of their own.
                return invoice.CustomerId is { } customerId ? (customerId.ToString(), invoice.CustomerName ?? string.Empty) : (WalkInKey, WalkInLabel);
            case SalesPerformanceGrouping.Zone:
                return (invoice.ZoneId.ToString(), invoice.ZoneName);
            default:
                var unit = grouping == SalesPerformanceGrouping.Day ? ReportPeriodUnit.Day : grouping == SalesPerformanceGrouping.Week ? ReportPeriodUnit.Week : ReportPeriodUnit.Month;
                var start = ReportPeriodBuckets.StartOf(invoice.Date, unit);
                return (ReportPeriodBuckets.Key(start), ReportPeriodBuckets.Label(start, unit));
        }
    }

    // A part of the invoice that is not a product: it adds to the revenue (negative for a discount or a price cut) and, when the invoice was returned, to the sales returns.
    private static void AddPart(Sums row, SalesPerformanceInvoice invoice, decimal amount, decimal priceCuts)
    {
        if (amount != 0m)
        {
            row.Add(invoice.Id, 0m, amount, priceCuts, invoice.HasReturn ? amount : 0m, 0m);
        }
    }

    private sealed class Sums(string key, string label, int rank)
    {
        private readonly HashSet<Guid> invoices = [];

        public string Key { get; } = key;
        public string Label { get; } = label;
        public int Rank { get; } = rank;
        public int InvoiceCount => invoices.Count;
        public decimal InvoicedKg { get; private set; }
        public decimal Revenue { get; private set; }
        public decimal PriceCuts { get; private set; }
        public decimal Returns { get; private set; }
        public decimal SoldKg { get; private set; }

        public void Add(Guid invoiceId, decimal kg, decimal revenue, decimal priceCuts, decimal returns, decimal soldKg)
        {
            invoices.Add(invoiceId);
            (InvoicedKg, Revenue, PriceCuts, Returns, SoldKg) = (InvoicedKg + kg, Revenue + revenue, PriceCuts + priceCuts, Returns + returns, SoldKg + soldKg);
        }
    }
}
