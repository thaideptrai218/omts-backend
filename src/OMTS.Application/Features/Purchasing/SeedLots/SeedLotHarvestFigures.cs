using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Harvest;

namespace OMTS.Application.Features.Purchasing.SeedLots;

/// <summary>What a seed lot yielded: the kg harvested, sold (invoiced less returned, cancelled invoices ignored) and returned from sale, attributed from the cycles the lot was hung in.</summary>
/// <param name="Estimated">True when a cycle was fed from several lots (its kg is shared in proportion to the pieces each hung) or a harvest lot is not weighed yet (its estimated kg is used).</param>
public sealed record SeedLotHarvestFigures(decimal HarvestedKg, decimal SoldKg, decimal ReturnedFromSaleKg, bool Estimated)
{
    public static readonly SeedLotHarvestFigures None = new(0m, 0m, 0m, false);

    /// <summary>
    /// The kg of each lot. A cycle fed from a nursery batch has one seed lot and keeps all its kg; a legacy cycle may be seeded from several lots, and each gets the share of the cycle's kg that its pieces are
    /// of the cycle's seeding. The kg of a harvest lot is the weighed one once weighed, else the estimated one (as <see cref="HarvestLotFigures"/> does); sold is invoiced less returned.
    /// </summary>
    public static IReadOnlyDictionary<Guid, SeedLotHarvestFigures> Attribute(
        IReadOnlyCollection<Guid> lineIds, SeedLotHarvestSources sources, IReadOnlyDictionary<Guid, LotSalesFigures> sales)
    {
        var cycles = sources.Seedings.GroupBy(s => s.CycleId).ToDictionary(g => g.Key, g => g.ToList());
        var lotsOfCycle = sources.HarvestLots.ToLookup(h => h.CycleId);
        var result = new Dictionary<Guid, SeedLotHarvestFigures>();
        foreach (var lineId in lineIds)
        {
            decimal harvested = 0m, sold = 0m, returned = 0m;
            var estimated = false;
            foreach (var (cycleId, seedings) in cycles.Where(c => c.Value.Any(s => s.SeedLotLineId == lineId)))
            {
                var lines = seedings.Select(s => s.SeedLotLineId).Distinct().Count();
                var total = seedings.Sum(s => s.Quantity);
                var mine = seedings.Where(s => s.SeedLotLineId == lineId).Sum(s => s.Quantity);
                var share = lines == 1 ? 1m : total > 0m ? mine / total : 1m / lines;
                estimated |= lines > 1;
                foreach (var lot in lotsOfCycle[cycleId])
                {
                    var lotSales = sales.TryGetValue(lot.HarvestLotId, out var found) ? found : LotSalesFigures.None;
                    var figures = HarvestLotFigures.Of(lot.EstimatedKg, lot.WeighedKg, null, true, lotSales);
                    harvested += (lot.WeighedKg ?? lot.EstimatedKg) * share;
                    sold += figures.SoldKg * share;
                    returned += lotSales.ReturnedKg * share;
                    estimated |= lot.WeighedKg is null;
                }
            }

            result[lineId] = new SeedLotHarvestFigures(Math.Round(harvested, 2), Math.Round(sold, 2), Math.Round(returned, 2), estimated);
        }

        return result;
    }
}
