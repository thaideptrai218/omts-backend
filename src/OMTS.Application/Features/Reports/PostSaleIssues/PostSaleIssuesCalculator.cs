using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Reports.PostSaleIssues;

/// <summary>The groups and the totals of a REPORT-03 selection.</summary>
public sealed record PostSaleCalculation(IReadOnlyList<PostSaleGroup> Groups, PostSaleGroup Totals);

/// <summary>
/// BR-REPORT-03 as pure functions, so the figures are unit tested and one rule serves the report, its events and its file. The events are already dated and limited to the period and the
/// zones by the query; this groups and adds them. By customer and by zone (the zone of the invoice) every figure is filled. By lot, only the returns (their number, returned kg and discarded
/// kg, from the return lines of the lot) are: a claim, a price cut and a cancellation apply to the whole invoice, which may sell several lots, so they are not split between lots (null).
/// </summary>
public static class PostSaleIssuesCalculator
{
    public const string WalkInKey = "walk-in";
    public const string TotalKey = "total";

    public const string ClaimType = "claim";
    public const string ReturnType = "return";
    public const string PriceCutType = "priceCut";
    public const string CancellationType = "cancellation";

    private const string WalkInLabel = "Walk-in customers";

    public static PostSaleCalculation Build(PostSaleFacts facts, PostSaleGrouping grouping)
    {
        var returnLots = facts.ReturnLots.ToLookup(l => l.ReturnId);
        var total = new Sums(TotalKey, "Total");
        var rows = new Dictionary<string, Sums>();

        Sums Row(string key, string label)
        {
            if (!rows.TryGetValue(key, out var row))
            {
                rows[key] = row = new Sums(key, label);
            }

            return row;
        }

        foreach (var e in facts.Events)
        {
            var lots = e.Type == PostSaleEventType.Return ? returnLots[e.Id].ToList() : [];
            total.Add(e, lots.Sum(l => l.ReturnedKg), lots.Sum(l => l.DiscardedKg));
            if (grouping != PostSaleGrouping.Lot)
            {
                var (key, label) = KeyOf(e, grouping);
                Row(key, label).Add(e, lots.Sum(l => l.ReturnedKg), lots.Sum(l => l.DiscardedKg));
                continue;
            }

            // By lot only the returns are counted, once for each lot they take back, with the kg of that lot.
            foreach (var lot in lots.GroupBy(l => l.LotId))
            {
                Row(lot.Key.ToString(), lot.First().LotCode).Add(e, lot.Sum(l => l.ReturnedKg), lot.Sum(l => l.DiscardedKg));
            }
        }

        var groups = rows.Values
            .OrderBy(r => r.Label, StringComparer.Ordinal)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => r.ToGroup(grouping == PostSaleGrouping.Lot))
            .ToList();
        return new PostSaleCalculation(groups, total.ToGroup(false));
    }

    /// <summary>The events of the group a report row names, newest first (REPORT-03.2). A key that names no group of this grouping gives none.</summary>
    public static IReadOnlyList<PostSaleItemRow> ItemsOf(PostSaleFacts facts, PostSaleGrouping grouping, string groupKey)
    {
        var returnLots = facts.ReturnLots.ToLookup(l => l.ReturnId);
        var invoiceLots = facts.InvoiceLots.ToLookup(l => l.InvoiceId);

        // By lot: the issues of the invoices that sell the lot (a return, of the lots of its return lines, which are those of the invoice).
        bool InGroup(PostSaleEvent e) => grouping == PostSaleGrouping.Lot
            ? (e.Type == PostSaleEventType.Return ? returnLots[e.Id].Select(l => l.LotId) : invoiceLots[e.InvoiceId].Select(l => l.LotId))
                .Any(id => string.Equals(id.ToString(), groupKey, StringComparison.OrdinalIgnoreCase))
            : string.Equals(KeyOf(e, grouping).Key, groupKey, StringComparison.OrdinalIgnoreCase);

        return facts.Events
            .Where(InGroup)
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.InvoiceCode, StringComparer.Ordinal).ThenBy(e => e.Type)
            .Select(e => new PostSaleItemRow(
                TypeName(e.Type), e.Date, e.InvoiceId, e.InvoiceCode,
                e.CustomerId is { } customerId ? new PostSaleItemCustomer(customerId, e.CustomerName ?? string.Empty) : null, e.Note, e.Amount, ClaimStatusName(e.ClaimStatusId)))
            .ToList();
    }

    private static (string Key, string Label) KeyOf(PostSaleEvent e, PostSaleGrouping grouping) => grouping == PostSaleGrouping.Zone
        ? (e.ZoneId.ToString(), e.ZoneName)
        // The invoices of walk-in customers have no profile and are one row of their own.
        : e.CustomerId is { } customerId ? (customerId.ToString(), e.CustomerName ?? string.Empty) : (WalkInKey, WalkInLabel);

    private static string TypeName(PostSaleEventType type) => type switch
    {
        PostSaleEventType.Claim => ClaimType,
        PostSaleEventType.Return => ReturnType,
        PostSaleEventType.PriceCut => PriceCutType,
        _ => CancellationType,
    };

    private static string? ClaimStatusName(int? statusId) => statusId switch
    {
        StatusIds.QualityClaim.AwaitingResponse => "awaitingResponse",
        StatusIds.QualityClaim.Rejected => "rejected",
        StatusIds.QualityClaim.Accepted => "accepted",
        _ => null,
    };

    private sealed class Sums(string key, string label)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
        private int claims, awaiting, rejected, accepted, returns, priceCuts, cancelled;
        private decimal returnedKg, discardedKg, returnsValue, priceCutsAmount, cancelledTotal;

        public void Add(PostSaleEvent e, decimal returned, decimal discarded)
        {
            switch (e.Type)
            {
                case PostSaleEventType.Claim:
                    claims++;
                    awaiting += e.ClaimStatusId == StatusIds.QualityClaim.AwaitingResponse ? 1 : 0;
                    rejected += e.ClaimStatusId == StatusIds.QualityClaim.Rejected ? 1 : 0;
                    accepted += e.ClaimStatusId == StatusIds.QualityClaim.Accepted ? 1 : 0;
                    break;
                case PostSaleEventType.Return:
                    returns++;
                    (returnedKg, discardedKg, returnsValue) = (returnedKg + returned, discardedKg + discarded, returnsValue + (e.Amount ?? 0m));
                    break;
                case PostSaleEventType.PriceCut:
                    priceCuts++;
                    priceCutsAmount += e.Amount ?? 0m;
                    break;
                default:
                    cancelled++;
                    cancelledTotal += e.Amount ?? 0m;
                    break;
            }
        }

        /// <param name="wholeInvoiceFiguresUnknown">By lot: what applies to a whole invoice is not split between lots, so it is left empty.</param>
        public PostSaleGroup ToGroup(bool wholeInvoiceFiguresUnknown) => wholeInvoiceFiguresUnknown
            ? new PostSaleGroup(Key, Label, null, null, null, null, returns, returnedKg, discardedKg, null, null, null, null, null)
            : new PostSaleGroup(Key, Label, claims, awaiting, rejected, accepted, returns, returnedKg, discardedKg, returnsValue, priceCuts, priceCutsAmount, cancelled, cancelledTotal);
    }
}
