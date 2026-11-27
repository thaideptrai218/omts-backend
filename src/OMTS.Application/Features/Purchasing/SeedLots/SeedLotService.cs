using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Purchasing.SeedLots;

/// <summary>
/// The seed lot register: one row per Seed line of a Completed receipt, with its quantities and its state worked out from them (never stored). Access follows the purchase receipt rules, which the API checks (a
/// receipt is not scoped to a zone, so neither is its lot); the zone filter only narrows the list. The database sums the quantities of every lot in a few set queries; the state, the sums, the order and the
/// page are worked out here over the lots the filters match, as the harvest lot list does.
/// </summary>
public sealed class SeedLotService(ISeedLotRegisterQuery lots, IStatusCatalog statusCatalog, IHarvestLotSalesQuery sales)
{
    public const int MaxPageSize = 100;

    public async Task<Result<SeedLotRegisterPage>> SearchAsync(SeedLotListQuery query, CancellationToken cancellationToken)
    {
        var read = Read(query);
        if (!read.IsSuccess)
        {
            return read.Error!;
        }

        var (filter, states, sort, descending) = read.Value;
        var rows = (await lots.SearchAsync(filter, cancellationToken)).Select(Row).ToList();
        var counts = Enum.GetValues<SeedLotState>().ToDictionary(s => s, s => rows.Count(r => r.State == s));
        var matched = states is null ? rows : rows.Where(r => states.Contains(r.State)).ToList();
        var totals = new SeedLotRegisterTotals(
            matched.Sum(r => r.ReceivedQty), matched.Sum(r => r.ReturnedQty), matched.Sum(r => r.NursingQty), matched.Sum(r => r.NurseryLossQty),
            matched.Sum(r => r.SeededQty), matched.Sum(r => r.IssuedOtherQty), matched.Sum(r => r.RemainingQty));
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        var items = await WithHarvestAsync(Order(matched, sort, descending).Skip((page - 1) * pageSize).Take(pageSize).ToList(), cancellationToken);
        return new SeedLotRegisterPage(items, matched.Count, page, pageSize, totals, counts);
    }

    /// <summary>The rows with the kg harvested and sold: two set queries for the lots and one for the sales of their harvest lots, whatever the number of rows.</summary>
    private async Task<IReadOnlyList<SeedLotRegisterRow>> WithHarvestAsync(IReadOnlyList<SeedLotRegisterRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return rows;
        }

        var sources = await lots.HarvestSourcesAsync(rows.Select(r => r.Id).ToList(), cancellationToken);
        var saleFigures = await sales.FiguresAsync(sources.HarvestLots.Select(h => h.HarvestLotId).Distinct().ToList(), cancellationToken);
        var figures = SeedLotHarvestFigures.Attribute(rows.Select(r => r.Id).ToList(), sources, saleFigures);
        return rows.Select(r => figures.TryGetValue(r.Id, out var f)
            ? r with { HarvestedKg = f.HarvestedKg, SoldKg = f.SoldKg, ReturnedFromSaleKg = f.ReturnedFromSaleKg, FiguresEstimated = f.Estimated }
            : r).ToList();
    }

    /// <summary>Every lot the filters match in the list's order, for the export (at most <paramref name="max"/>).</summary>
    public async Task<Result<IReadOnlyList<SeedLotRegisterRow>>> ListAllAsync(SeedLotListQuery query, int max, CancellationToken cancellationToken)
    {
        var read = Read(query);
        if (!read.IsSuccess)
        {
            return read.Error!;
        }

        var (filter, states, sort, descending) = read.Value;
        var rows = (await lots.SearchAsync(filter, cancellationToken)).Select(Row).Where(r => states is null || states.Contains(r.State)).ToList();
        return Result<IReadOnlyList<SeedLotRegisterRow>>.Success(Order(rows, sort, descending).Take(max).ToList());
    }

    public async Task<Result<SeedLotDetail>> GetAsync(Guid lineId, CancellationToken cancellationToken)
    {
        var found = await lots.GetAsync(lineId, cancellationToken);
        if (found is null)
        {
            return AppError.NotFound;
        }

        var detail = await lots.DetailAsync(lineId, cancellationToken);
        var row = (await WithHarvestAsync([Row(found)], cancellationToken))[0];
        return new SeedLotDetail(
            row,
            detail.NurseryBatches.OrderBy(b => b.StartDate).ThenBy(b => b.Code, StringComparer.Ordinal)
                .Select(b => new SeedLotNurseryBatchView(b.Id, b.Code, b.StatusId, statusCatalog.DisplayNameOf(b.StatusId), b.QuantityIn, b.Survivors, b.StartDate)).ToList(),
            detail.Seedings.OrderBy(s => s.SeedingDate).ThenBy(s => s.RaftCode, StringComparer.Ordinal)
                .Select(s => new SeedLotSeedingView(s.Id, s.CycleId, s.RaftCode, s.ZoneCode, s.Quantity, StartOfDay(s.SeedingDate))).ToList(),
            detail.Returns.OrderBy(r => r.ReturnedAt).Select(r => new SeedLotReturnView(r.Id, r.Code, r.Quantity, r.ReturnedAt, r.StatusId)).ToList(),
            detail.Issues.OrderBy(i => i.IssuedAt).Select(i => new SeedLotIssueView(i.Id, i.Code, i.Kind, i.Quantity, i.IssuedAt)).ToList(),
            detail.Cycles.Select(c => new SeedLotCycleView(c.Id, c.RaftCode, c.ZoneCode, c.StatusId, c.ProductCode)).ToList(),
            detail.HarvestLots.Select(h => new SeedLotHarvestLotView(h.Id, h.LotCode, h.StatusId, h.WeighedKg, h.EstimatedKg)).ToList());
    }

    /// <summary>The lot as the list shows it: the quantities and the state worked out from them.</summary>
    public static SeedLotRegisterRow Row(SeedLotFiguresRow r)
    {
        var f = r.Figures;
        return new SeedLotRegisterRow(
            r.LineId, DocumentCodes.SeedLotCode(r.ReceiptCode, r.LineNumber), r.ReceiptId, r.ReceiptCode, r.ReceivedAt,
            new SeedLotNamedRef(r.SupplierId, r.SupplierCode, r.SupplierName), new SeedLotNamedRef(r.ItemId, r.ItemCode, r.ItemName),
            r.ProductId is { } productId ? new SeedLotNamedRef(productId, r.ProductCode ?? string.Empty, r.ProductName ?? string.Empty) : null,
            r.UnitName, f.Received, f.Returned, f.Nursing, f.NurseryLoss, f.Seeded, f.IssuedOther, f.Remaining, f.State, f.LastUsedAt, f.Available);
    }

    /// <summary>The lot code breaks every tie, so a page never repeats or skips a lot.</summary>
    internal static IEnumerable<SeedLotRegisterRow> Order(IEnumerable<SeedLotRegisterRow> rows, string sort, bool descending)
    {
        var byCode = new CodeComparer();
        var ordered = sort switch
        {
            "receivedat" => descending ? rows.OrderByDescending(r => r.ReceivedAt) : rows.OrderBy(r => r.ReceivedAt),
            "remaining" => descending ? rows.OrderByDescending(r => r.RemainingQty) : rows.OrderBy(r => r.RemainingQty),
            _ => descending ? rows.OrderByDescending(r => r.SeedLotCode, byCode) : rows.OrderBy(r => r.SeedLotCode, byCode),
        };
        return descending ? ordered.ThenByDescending(r => r.SeedLotCode, byCode) : ordered.ThenBy(r => r.SeedLotCode, byCode);
    }

    /// <summary>PN000012-2 before PN000012-10: the receipt code as text, then the line number as a number.</summary>
    private sealed class CodeComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            var (a, b) = (Split(x), Split(y));
            var byReceipt = string.CompareOrdinal(a.Receipt, b.Receipt);
            return byReceipt != 0 ? byReceipt : a.Line.CompareTo(b.Line);
        }

        private static (string Receipt, int Line) Split(string? code)
        {
            var dash = code?.LastIndexOf('-') ?? -1;
            return dash > 0 && int.TryParse(code![(dash + 1)..], out var line) ? (code[..dash], line) : (code ?? string.Empty, 0);
        }
    }

    private static DateTimeOffset StartOfDay(DateOnly day) => BusinessClock.FromLocal(day.ToDateTime(TimeOnly.MinValue));

    private static Result<(SeedLotRegisterFilter Filter, IReadOnlySet<SeedLotState>? States, string Sort, bool Descending)> Read(SeedLotListQuery query)
    {
        if (query.From is { } from && query.To is { } to && from > to)
        {
            return AppError.Invalid("Period");
        }

        IReadOnlySet<SeedLotState>? states = null;
        if (!string.IsNullOrWhiteSpace(query.State))
        {
            var parsed = new HashSet<SeedLotState>();
            foreach (var part in query.State.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<SeedLotState>(part, ignoreCase: true, out var state) || !Enum.IsDefined(state) || part.All(char.IsDigit))
                {
                    return AppError.Invalid("State", "Use Unused, Nursing, PartlySeeded, FullySeeded, FullyReturned, FullyLost or UsedUp.");
                }

                parsed.Add(state);
            }

            states = parsed.Count == 0 ? null : parsed;
        }

        var sort = query.Sort?.Trim().ToLowerInvariant() switch
        {
            null or "" or "code" => "code",
            "receivedat" => "receivedat",
            "remaining" => "remaining",
            _ => null,
        };
        if (sort is null)
        {
            return AppError.Invalid("Sort", "Use code, receivedAt or remaining.");
        }

        if (query.SortDir?.Trim().ToLowerInvariant() is not (null or "" or "asc" or "desc"))
        {
            return AppError.Invalid("Sort direction", "Use asc or desc.");
        }

        // The register is not scoped to a zone (it follows the purchase receipt rules), so every zone asked for is kept.
        var zones = PermittedZones.Choose(query.ZoneId, query.ZoneIds);
        if (!zones.IsSuccess)
        {
            return zones.Error!;
        }

        var descending = !string.Equals(query.SortDir?.Trim(), "asc", StringComparison.OrdinalIgnoreCase);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        return (new SeedLotRegisterFilter(query.From, query.To, query.SupplierId, query.ItemId, zones.Value!.Count > 0 ? zones.Value : null, search), states, sort, descending);
    }
}
