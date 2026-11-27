using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Application.Features.Reports.Purchases;

/// <summary>
/// The purchases figures as pure functions. A document is worth its lines less its discount (the goods); its input VAT is counted apart and added in the net. Grouped by supplier a document is one row of its supplier; grouped by item each item's lines
/// are its own rows and the discounts of whole documents are the group <see cref="DiscountsKey"/> (negative), so that the groups add up to the totals.
/// </summary>
public static class PurchaseReportCalculator
{
    public const string DiscountsKey = "receipt-discounts";
    public const string DiscountsLabel = "Document discounts";

    public static (IReadOnlyList<PurchaseReportGroup> Groups, PurchaseReportFigures Totals) Build(IReadOnlyList<PurchaseReportDocument> docs, PurchaseReportGrouping grouping)
    {
        var rows = new Dictionary<string, Sums>();
        Sums Row(string key, string label, string? code, string? unit)
        {
            if (!rows.TryGetValue(key, out var row))
            {
                rows[key] = row = new Sums(key, label, code, unit);
            }

            return row;
        }

        foreach (var doc in docs)
        {
            if (grouping == PurchaseReportGrouping.Supplier)
            {
                Row(doc.SupplierId.ToString(), doc.SupplierName, null, null).Add(doc.Id, doc.IsReturn, Value(doc), 0m, Vat(doc));
                continue;
            }

            foreach (var item in doc.Lines.GroupBy(l => l.ItemId))
            {
                var first = item.First();
                Row(item.Key.ToString(), first.ItemName, first.ItemCode, first.Unit).Add(doc.Id, doc.IsReturn, item.Sum(l => l.Amount), item.Sum(l => l.Quantity), item.Sum(l => l.Vat));
            }

            if (doc.Discount != 0m)
            {
                Row(DiscountsKey, DiscountsLabel, null, null).Add(doc.Id, doc.IsReturn, -doc.Discount, 0m, 0m);
            }
        }

        var withQuantity = grouping == PurchaseReportGrouping.Item;
        var groups = rows.Values
            .OrderBy(r => r.Key == DiscountsKey ? 1 : 0)
            .ThenByDescending(r => r.Received - r.Returned)
            .ThenBy(r => r.Label, StringComparer.CurrentCulture)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => new PurchaseReportGroup(
                r.Key, r.Label, r.Code, r.Unit, r.ReceiptCount, r.ReturnCount, r.Received, r.Returned, r.Received - r.Returned + (r.ReceivedVat - r.ReturnedVat),
                withQuantity ? r.ReceivedQuantity : null, withQuantity ? r.ReturnedQuantity : null,
                r.ReceivedVat - r.ReturnedVat, r.ReceivedVat, r.ReturnedVat, r.Received - r.Returned))
            .ToList();
        var received = docs.Where(d => !d.IsReturn).Sum(Value);
        var returned = docs.Where(d => d.IsReturn).Sum(Value);
        var receivedVat = docs.Where(d => !d.IsReturn).Sum(Vat);
        var returnedVat = docs.Where(d => d.IsReturn).Sum(Vat);
        var totals = new PurchaseReportFigures(
            docs.Count(d => !d.IsReturn), docs.Count(d => d.IsReturn), received, returned, received - returned + (receivedVat - returnedVat),
            withQuantity ? groups.Sum(g => g.ReceivedQuantity) : null, withQuantity ? groups.Sum(g => g.ReturnedQuantity) : null,
            receivedVat - returnedVat, receivedVat, returnedVat, received - returned);
        return (groups, totals);
    }

    /// <summary>The receipts and returns of the group a report row names, newest first. A key that names no group gives none.</summary>
    public static IReadOnlyList<PurchaseDocumentRow> DocumentsOf(IReadOnlyList<PurchaseReportDocument> docs, PurchaseReportGrouping grouping, string groupKey)
    {
        (decimal Value, decimal Vat)? ValueIn(PurchaseReportDocument doc)
        {
            if (grouping == PurchaseReportGrouping.Supplier)
            {
                return string.Equals(doc.SupplierId.ToString(), groupKey, StringComparison.OrdinalIgnoreCase) ? (Value(doc), Vat(doc)) : null;
            }

            if (groupKey == DiscountsKey)
            {
                return doc.Discount != 0m ? (doc.Discount, 0m) : null;
            }

            var lines = doc.Lines.Where(l => string.Equals(l.ItemId.ToString(), groupKey, StringComparison.OrdinalIgnoreCase)).ToList();
            return lines.Count > 0 ? (lines.Sum(l => l.Amount), lines.Sum(l => l.Vat)) : null;
        }

        return docs
            .Select(d => (Doc: d, Found: ValueIn(d)))
            .Where(x => x.Found is not null)
            .OrderByDescending(x => x.Doc.Date).ThenByDescending(x => x.Doc.Code, StringComparer.Ordinal)
            .Select(x => new PurchaseDocumentRow(
                x.Doc.Id, x.Doc.Code, x.Doc.Date, x.Doc.IsReturn, x.Doc.ReceiptKind?.ToString(), new PurchaseDocumentSupplier(x.Doc.SupplierId, x.Doc.SupplierName),
                x.Found!.Value.Value, x.Found.Value.Vat))
            .ToList();
    }

    private static decimal Value(PurchaseReportDocument doc) => doc.Lines.Sum(l => l.Amount) - doc.Discount;

    private static decimal Vat(PurchaseReportDocument doc) => doc.Lines.Sum(l => l.Vat);

    private sealed class Sums(string key, string label, string? code, string? unit)
    {
        private readonly HashSet<Guid> receipts = [];
        private readonly HashSet<Guid> returns = [];

        public string Key { get; } = key;
        public string Label { get; } = label;
        public string? Code { get; } = code;
        public string? Unit { get; } = unit;
        public int ReceiptCount => receipts.Count;
        public int ReturnCount => returns.Count;
        public decimal Received { get; private set; }
        public decimal Returned { get; private set; }
        public decimal ReceivedQuantity { get; private set; }
        public decimal ReturnedQuantity { get; private set; }
        public decimal ReceivedVat { get; private set; }
        public decimal ReturnedVat { get; private set; }

        public void Add(Guid documentId, bool isReturn, decimal value, decimal quantity, decimal vat)
        {
            if (isReturn)
            {
                returns.Add(documentId);
                (Returned, ReturnedQuantity, ReturnedVat) = (Returned + value, ReturnedQuantity + quantity, ReturnedVat + vat);
            }
            else
            {
                receipts.Add(documentId);
                (Received, ReceivedQuantity, ReceivedVat) = (Received + value, ReceivedQuantity + quantity, ReceivedVat + vat);
            }
        }
    }
}
