using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Repositories;

/// <summary>
/// The harvest lot list read side: a page and the sums of the lots the filters match are worked out by SQL, so the list never loads every lot of the cooperative. The sold, remaining and lost kg
/// follow <c>HarvestLotFigures.Shown</c>: the kg on the invoices that are not cancelled and on the returns that stand, over the weighed kg (the estimate before the weighing).
/// </summary>
internal sealed partial class HarvestLotRepository
{
    private static readonly Expression<Func<HarvestLot, HarvestLotListRow>> Row = l => new HarvestLotListRow(
        l.Id, l.LotCode, l.HarvestedAt, l.HarvestDate, l.Raft.ZoneId, l.Raft.Zone.Code, l.RaftId, l.Raft.Code, l.CycleId, l.CageCount, l.EstimatedKg,
        l.WeighedKg, l.WeighedAt, l.StatusId, l.CloseReason, l.CloseDate, l.LeftoverKg, l.RecordedByAccountId);

    public async Task<IReadOnlyList<HarvestLotListRow>> SearchPageAsync(
        HarvestLotFilter filter, HarvestLotSort sort, bool descending, int skip, int take, CancellationToken cancellationToken)
    {
        var lots = Matching(filter);
        if (sort == HarvestLotSort.RemainingKg)
        {
            var figures = WithFigures(lots);
            var byRemaining = descending ? figures.OrderByDescending(r => r.RemainingKg) : figures.OrderBy(r => r.RemainingKg);
            var ordered = ThenByCode(byRemaining, descending, r => r.LotCode, r => EF.Functions.Collate(r.LotCode, OrdinalCollation));
            var ids = await ordered.Skip(skip).Take(take).Select(r => r.Id).ToListAsync(cancellationToken);
            var rows = ids.Count == 0 ? [] : await db.HarvestLots.AsNoTracking().Where(l => ids.Contains(l.Id)).Select(Row).ToListAsync(cancellationToken);
            var position = ids.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
            return [.. rows.OrderBy(r => position[r.Id])];
        }

        Expression<Func<HarvestLot, string>> code = l => l.LotCode;
        Expression<Func<HarvestLot, string>> ordinalCode = l => EF.Functions.Collate(l.LotCode, OrdinalCollation);
        var sorted = sort == HarvestLotSort.HarvestedAt
            ? (descending ? lots.OrderByDescending(l => l.HarvestedAt) : lots.OrderBy(l => l.HarvestedAt))
            : OrderByCode(lots, descending, code, ordinalCode);
        var page = ThenByCode(sorted, descending, code, ordinalCode);
        return await page.Skip(skip).Take(take).Select(Row).ToListAsync(cancellationToken);
    }

    // The list has always ordered lot codes ordinally (KV1-B01-... before KV10-B01-...). A SQL Server column collation sorts nvarchar by word rules (hyphens nearly ignored, case folded), so on
    // SQL Server the code is ordered by a binary collation; the in-memory provider has no collations and keeps the plain column.
    private const string OrdinalCollation = "Latin1_General_100_BIN2";

    private IOrderedQueryable<T> OrderByCode<T>(IQueryable<T> source, bool descending, Expression<Func<T, string>> code, Expression<Func<T, string>> ordinalCode)
    {
        var key = db.Database.IsSqlServer() ? ordinalCode : code;
        return descending ? source.OrderByDescending(key) : source.OrderBy(key);
    }

    private IOrderedQueryable<T> ThenByCode<T>(IOrderedQueryable<T> source, bool descending, Expression<Func<T, string>> code, Expression<Func<T, string>> ordinalCode)
    {
        var key = db.Database.IsSqlServer() ? ordinalCode : code;
        return descending ? source.ThenByDescending(key) : source.ThenBy(key);
    }

    public async Task<HarvestLotListSums> SumsAsync(HarvestLotFilter filter, CancellationToken cancellationToken)
    {
        var sums = await WithFigures(Matching(filter))
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count = g.Count(),
                CageCount = g.Sum(r => r.CageCount),
                EstimatedKg = g.Sum(r => r.EstimatedKg),
                WeighedKg = g.Sum(r => r.WeighedKg ?? 0m),
                Invoiced = g.Sum(r => r.Invoiced),
                Returned = g.Sum(r => r.Returned),
                Remaining = g.Sum(r => r.RemainingKg),
                Leftover = g.Sum(r => r.LeftoverKg ?? 0m),
                Discarded = g.Sum(r => r.Discarded),
            })
            .FirstOrDefaultAsync(cancellationToken);
        return sums is null
            ? HarvestLotListSums.None
            : new HarvestLotListSums(sums.Count, sums.CageCount, sums.EstimatedKg, sums.WeighedKg, sums.Invoiced - sums.Returned, sums.Remaining, sums.Leftover + sums.Discarded);
    }

    private IQueryable<HarvestLot> Matching(HarvestLotFilter filter)
    {
        var search = filter.Search?.Trim();
        var zones = filter.ZoneIds?.ToList();
        return db.HarvestLots.AsNoTracking().Where(l =>
            (filter.From == null || l.HarvestDate >= filter.From)
            && (filter.To == null || l.HarvestDate <= filter.To)
            && (filter.ZoneId == null || l.Raft.ZoneId == filter.ZoneId)
            && (zones == null || zones.Contains(l.Raft.ZoneId))
            && (filter.RaftId == null || l.RaftId == filter.RaftId)
            && (filter.ProductId == null || l.ProductLines.Any(p => p.ProductId == filter.ProductId))
            && (filter.StatusId == null || l.StatusId == filter.StatusId)
            && (filter.Weighed == null || (l.WeighedKg != null) == filter.Weighed)
            && (filter.CloseReason == null || l.CloseReason == filter.CloseReason)
            && (filter.HasPlan == null || (l.HarvestPlanLineId != null) == filter.HasPlan)
            && (string.IsNullOrEmpty(search) || l.LotCode.Contains(search) || (l.HarvestPlanLine != null && l.HarvestPlanLine.Plan.Code.Contains(search))));
    }

    /// <summary>
    /// Each lot with the kg of its invoices that are not cancelled and of its returns that stand (joined from one grouped read of each, never a subquery inside an aggregate), and the remaining kg of
    /// <c>HarvestLotFigures.Shown</c>: none while the lot waits for its weighing, else the base kg less the invoiced kg plus what went back for sale, never below 0. All of it is SQL.
    /// </summary>
    private IQueryable<LotWithSales> WithFigures(IQueryable<HarvestLot> lots)
    {
        var invoiced = db.SalesInvoiceLines
            .Where(x => x.Invoice.StatusId != StatusIds.SalesInvoice.Cancelled)
            .GroupBy(x => x.HarvestLotId)
            .Select(g => new { LotId = g.Key, Kg = g.Sum(x => x.Kg) });
        var returned = db.SalesReturns.Where(SalesReturn.Standing).SelectMany(x => x.Lines)
            .GroupBy(x => x.HarvestLotId)
            .Select(g => new { LotId = g.Key, Returned = g.Sum(x => x.ReturnedKg), Discarded = g.Sum(x => x.DiscardedKg) });
        return
            from l in lots
            join i in invoiced on l.Id equals i.LotId into invoicedOfLot
            from i in invoicedOfLot.DefaultIfEmpty()
            join r in returned on l.Id equals r.LotId into returnedOfLot
            from r in returnedOfLot.DefaultIfEmpty()
            select new
            {
                l.Id,
                l.LotCode,
                l.CageCount,
                l.EstimatedKg,
                l.StatusId,
                l.WeighedKg,
                l.LeftoverKg,
                Invoiced = (decimal?)i.Kg ?? 0m,
                Returned = (decimal?)r.Returned ?? 0m,
                Discarded = (decimal?)r.Discarded ?? 0m,
            } into x
            select new LotWithSales
            {
                Id = x.Id,
                LotCode = x.LotCode,
                CageCount = x.CageCount,
                EstimatedKg = x.EstimatedKg,
                WeighedKg = x.WeighedKg,
                LeftoverKg = x.LeftoverKg,
                Invoiced = x.Invoiced,
                Returned = x.Returned,
                Discarded = x.Discarded,
                RemainingKg = x.StatusId == StatusIds.HarvestLot.AwaitingWeighing && x.WeighedKg == null
                    ? 0m
                    : (x.WeighedKg ?? x.EstimatedKg) - x.Invoiced + (x.Returned - x.Discarded) > 0m
                        ? (x.WeighedKg ?? x.EstimatedKg) - x.Invoiced + (x.Returned - x.Discarded)
                        : 0m,
            };
    }

    private sealed class LotWithSales
    {
        public Guid Id { get; init; }

        public string LotCode { get; init; } = string.Empty;

        public decimal CageCount { get; init; }

        public decimal EstimatedKg { get; init; }

        public decimal? WeighedKg { get; init; }

        public decimal? LeftoverKg { get; init; }

        public decimal Invoiced { get; init; }

        public decimal Returned { get; init; }

        public decimal Discarded { get; init; }

        public decimal RemainingKg { get; init; }
    }
}
