using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.CashBook;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Reports.EndOfDay;

/// <summary>
/// The figures of the end-of-day report worked out from the facts the query read, as pure functions: the sales by kind of document, the goods per product with the discount of each invoice
/// shared over its lines, and the money by fund. An order is not revenue: its deposits count as money received, its value counts nowhere in the revenue.
/// </summary>
public static class EndOfDayCalculator
{
    public const string OrderKind = "Order";
    public const string InvoiceKind = "Invoice";
    public const string ReturnKind = "Return";

    public static EndOfDaySales SalesOf(EndOfDaySalesFacts facts)
    {
        var orders = facts.Orders.Select(o => new EndOfDaySalesRow(
            o.Id, o.Code, o.At, CustomerOf(o.CustomerId, o.CustomerName), o.ZoneName, o.Kg, o.Subtotal, o.Discount, 0m, o.Deposits, 0m)).ToList();
        var invoices = facts.Invoices.Select(i => new EndOfDaySalesRow(
            i.Id, i.Code, i.At, CustomerOf(i.CustomerId, i.CustomerName), i.ZoneName, i.Kg, i.Subtotal, i.Discount, i.OtherCharges, i.PaidInPeriod,
            Math.Max(0m, i.Total - i.ReturnedValue - i.PaidTotal))).ToList();
        var returns = facts.Returns.Select(r => new EndOfDaySalesRow(
            r.Id, r.Code, r.At, CustomerOf(r.CustomerId, r.CustomerName), r.ZoneName, -r.Kg, -GoodsPartOf(r), 0m, -OtherChargesPartOf(r), 0m, 0m)).ToList();

        var invoiceRevenue = invoices.Sum(i => i.Revenue);
        var revenue = invoiceRevenue + returns.Sum(r => r.Revenue);
        var discount = invoices.Sum(i => i.Discount);
        var totals = new EndOfDaySalesTotals(
            invoices.Sum(i => i.QuantityKg) + returns.Sum(r => r.QuantityKg), revenue, discount, invoices.Sum(i => i.OtherCharges) + returns.Sum(r => r.OtherCharges), invoices.Sum(i => i.Paid) + orders.Sum(o => o.Paid),
            invoices.Sum(i => i.Debt), revenue - discount);
        return new EndOfDaySales([GroupOf(OrderKind, orders), GroupOf(InvoiceKind, invoices), GroupOf(ReturnKind, returns)], totals);
    }

    /// <summary>The short form of the sales: counts and money, the order value and deposits apart from the revenue.</summary>
    public static EndOfDaySalesSummary SummaryOf(EndOfDaySales sales)
    {
        var orders = sales.Groups.Single(g => g.Kind == OrderKind);
        var invoices = sales.Groups.Single(g => g.Kind == InvoiceKind);
        var returns = sales.Groups.Single(g => g.Kind == ReturnKind);
        var returnValue = -returns.Revenue;
        return new EndOfDaySalesSummary(
            orders.Count, orders.Revenue - orders.Discount, orders.Paid, invoices.Count, invoices.Revenue, invoices.Discount, returns.Count, returnValue, invoices.Revenue - invoices.Discount - returnValue,
            sales.Totals.Paid, sales.Totals.Debt);
    }

    /// <summary>
    /// A return gives the whole invoice back (its stored value is the invoice total, other charges included), so its other charges go to the "Thu khác" column and only the rest
    /// counts against the revenue: an invoice sold and fully returned then nets to zero revenue.
    /// </summary>
    internal static decimal OtherChargesPartOf(EndOfDayReturnFact r) => Math.Clamp(r.InvoiceOtherCharges, 0m, Math.Max(0m, r.Value));

    internal static decimal GoodsPartOf(EndOfDayReturnFact r) => r.Value - OtherChargesPartOf(r);

    private static EndOfDaySalesGroup GroupOf(string kind, List<EndOfDaySalesRow> rows) => new(
        kind, rows.Count, rows.Sum(r => r.QuantityKg), rows.Sum(r => r.Revenue), rows.Sum(r => r.Discount), rows.Sum(r => r.OtherCharges), rows.Sum(r => r.Paid), rows.Sum(r => r.Debt), rows);

    private static EndOfDayCustomer? CustomerOf(Guid? id, string? name) => id is { } customerId ? new EndOfDayCustomer(customerId, name ?? string.Empty) : null;

    /// <summary>
    /// The goods per product: what the invoices of the period sold and what the returns of the period sent back, each with the discount of its invoice (discount and price cuts) shared over the
    /// lines of the invoice. <paramref name="productIds"/> and <paramref name="categoryIds"/> (the chosen categories and what is below them) keep only those lines, after the sharing, so a
    /// filter never changes the value of a line. Products by kg sold, most first.
    /// </summary>
    public static EndOfDayGoods GoodsOf(
        EndOfDayGoodsFacts facts, IReadOnlyList<EndOfDayLot> lots, IReadOnlyCollection<Guid> productIds, IReadOnlyCollection<Guid> categoryIds)
    {
        var net = NetAmounts(facts.Lines.Concat(facts.ReturnedInvoiceLines));
        bool Wanted(EndOfDayLineFact l) =>
            (productIds.Count == 0 || productIds.Contains(l.ProductId)) && (categoryIds.Count == 0 || (l.CategoryId is { } c && categoryIds.Contains(c)));

        var byProduct = new Dictionary<Guid, ProductAccumulator>();
        ProductAccumulator Of(EndOfDayLineFact l) =>
            byProduct.TryGetValue(l.ProductId, out var found) ? found : byProduct[l.ProductId] = new ProductAccumulator(l.ProductId, l.ProductCode, l.ProductName);

        foreach (var line in facts.Lines.Where(Wanted))
        {
            var product = Of(line);
            product.SoldKg += line.Kg;
            product.Revenue += net[line.Id];
            product.Add(InvoiceKind, line.InvoiceId, line.InvoiceCode, line.InvoicedAt, line.CustomerName, line.Kg, net[line.Id]);
        }

        var linesById = facts.Lines.Concat(facts.ReturnedInvoiceLines).GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var returned in facts.ReturnedLines)
        {
            if (!linesById.TryGetValue(returned.InvoiceLineId, out var line) || !Wanted(line))
            {
                continue;
            }

            var product = Of(line);
            product.ReturnedKg += returned.ReturnedKg;
            product.ReturnValue += net[line.Id];
            product.Add(ReturnKind, returned.ReturnId, returned.ReturnCode, returned.At, returned.CustomerName, returned.ReturnedKg, net[line.Id]);
        }

        var products = byProduct.Values
            .OrderByDescending(p => p.SoldKg).ThenBy(p => p.Name, StringComparer.CurrentCulture)
            .Select(p => p.ToRow())
            .ToList();
        var lotRows = lots.Select(l => new EndOfDayLotRow(l.Id, l.LotCode, l.ZoneName, l.WeighedKg, l.WeighedAt)).ToList();
        var totals = new EndOfDayGoodsTotals(
            products.Sum(p => p.SoldKg), products.Sum(p => p.Revenue), products.Sum(p => p.ReturnedKg), products.Sum(p => p.ReturnValue), products.Sum(p => p.NetRevenue),
            lotRows.Count, lotRows.Sum(l => l.WeighedKg));
        return new EndOfDayGoods(products, lotRows, totals);
    }

    public static EndOfDayGoodsSummary SummaryOf(EndOfDayGoods goods) =>
        new(goods.Products.Count, goods.Totals.SoldKg, goods.Totals.ReturnedKg, goods.Totals.LotCount, goods.Totals.WeighedKg);

    /// <summary>The amount of every invoice line after its share of the discount of its invoice.</summary>
    private static Dictionary<Guid, decimal> NetAmounts(IEnumerable<EndOfDayLineFact> lines)
    {
        var net = new Dictionary<Guid, decimal>();
        foreach (var invoice in lines.GroupBy(l => l.InvoiceId))
        {
            var ordered = invoice.GroupBy(l => l.Id).Select(g => g.First()).OrderBy(l => l.LineNumber).ToList();
            var shares = AllocateDiscount(ordered.Select(l => l.Amount).ToList(), ordered[0].InvoiceDiscount);
            for (var i = 0; i < ordered.Count; i++)
            {
                net[ordered[i].Id] = ordered[i].Amount - shares[i];
            }
        }

        return net;
    }

    /// <summary>
    /// Shares <paramref name="discount"/> over lines in proportion to their amounts, each share rounded to the whole dong and the rest on the last line, so the shares add up to the discount.
    /// A discount above the sum of the amounts is cut to it (a line never goes below 0); no discount or no amount gives no share.
    /// </summary>
    public static IReadOnlyList<decimal> AllocateDiscount(IReadOnlyList<decimal> amounts, decimal discount)
    {
        var shares = new decimal[amounts.Count];
        var sum = amounts.Sum();
        if (amounts.Count == 0 || discount <= 0m || sum <= 0m)
        {
            return shares;
        }

        var total = Math.Min(discount, sum);
        var given = 0m;
        for (var i = 0; i < amounts.Count - 1; i++)
        {
            shares[i] = Math.Round(total * amounts[i] / sum, 0, MidpointRounding.AwayFromZero);
            given += shares[i];
        }

        shares[^1] = total - given;
        return shares;
    }

    /// <summary>The cash book lines of the period as report lines.</summary>
    public static EndOfDayMoney MoneyOf(IReadOnlyList<EndOfDayMoneyEntry> entries)
    {
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

    public static EndOfDayMoneyEntry EntryOf(CashBookRow row) => new(
        row.Source.ToString(), row.Code, row.OccurredAt, row.Direction.ToString(), row.Fund.ToString(), NullIfEmpty(row.Type.Name), row.Party?.Kind.ToString(), NullIfEmpty(row.Party?.Name),
        row.Amount, row.Note, NullIfEmpty(row.CreatedBy?.Name));

    /// <summary>The chosen categories and everything below them.</summary>
    public static IReadOnlyCollection<Guid> WithDescendants(IReadOnlyCollection<Guid> chosen, IReadOnlyList<EndOfDayCategory> all)
    {
        var result = chosen.ToHashSet();
        var added = true;
        while (added)
        {
            added = false;
            foreach (var category in all)
            {
                if (category.ParentId is { } parent && result.Contains(parent) && result.Add(category.Id))
                {
                    added = true;
                }
            }
        }

        return result;
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private sealed class ProductAccumulator(Guid productId, string code, string name)
    {
        private readonly Dictionary<(string Kind, Guid Document), EndOfDayGoodsDetail> details = [];

        public string Name { get; } = name;

        public decimal SoldKg { get; set; }

        public decimal Revenue { get; set; }

        public decimal ReturnedKg { get; set; }

        public decimal ReturnValue { get; set; }

        /// <summary>One detail per document: the lines of the same product on one invoice add up.</summary>
        public void Add(string kind, Guid documentId, string code, DateTimeOffset at, string? customerName, decimal kg, decimal value)
        {
            var key = (kind, documentId);
            details[key] = details.TryGetValue(key, out var found)
                ? found with { Kg = found.Kg + kg, Value = found.Value + value }
                : new EndOfDayGoodsDetail(documentId, code, kind, at, customerName, kg, value);
        }

        public EndOfDayProductRow ToRow() => new(
            productId, code, Name, "kg", SoldKg, Revenue, ReturnedKg, ReturnValue, Revenue - ReturnValue,
            details.Values.OrderBy(d => d.At).ThenBy(d => d.Code, StringComparer.Ordinal).ToList());
    }
}
