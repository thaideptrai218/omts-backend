using System.Globalization;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Cultivation.Nursery;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>The groups and totals of HARVEST-04 from lots that have been chosen.</summary>
public sealed record HarvestYieldResult(IReadOnlyList<HarvestYieldGroup> Groups, HarvestYieldTotals Totals);

/// <summary>What a cycle holds beyond the lots of the report: the seed hung and the kg of all its lots, for the plan figures of a group, and its nursery batch when it was created from one.</summary>
public sealed record YieldCycleFigures(CycleSeedTotal? Seed, decimal HarvestedKg, NurseryCycleFacts? Nursery = null);

/// <summary>
/// HARVEST-04 figures (BR-HARVEST-07), pure: each lot's figures come from <see cref="HarvestLotFigures"/>, and a raft, zone or cycle is the plain total of its lots, never rounded and never
/// split (each lot comes from one raft; the zone of a lot is the zone of its raft). A group, and the totals, are not final while one of their lots is still for sale.
/// </summary>
public static class HarvestYieldCalculator
{
    /// <param name="lots">With raft, zone and cycle loaded.</param>
    /// <param name="sales">The sales port's figures of each lot.</param>
    /// <param name="statusName">The display name of a status id (lot or cycle).</param>
    public static HarvestYieldResult Build(
        IReadOnlyList<HarvestLot> lots, IReadOnlyDictionary<Guid, LotSalesFigures> sales, HarvestYieldGrouping groupBy, DateOnly from, DateOnly to, Func<int, string> statusName,
        IReadOnlyDictionary<Guid, YieldCycleFigures>? cycleFigures = null)
    {
        var rows = lots
            .OrderBy(l => l.HarvestDate).ThenBy(l => l.HarvestedAt).ThenBy(l => l.LotCode, StringComparer.Ordinal)
            .Select(l => (Lot: l, View: LotOf(l, sales.GetValueOrDefault(l.Id, LotSalesFigures.None), from, to, statusName)))
            .ToList();
        var groups = rows
            .GroupBy(r => KeyOf(r.Lot, groupBy))
            .Select(g => GroupOf(g.First().Lot, g.Select(r => r.View).ToList(), groupBy, statusName, g.Select(r => r.Lot.Cycle).DistinctBy(c => c.Id).ToList(), cycleFigures))
            .OrderBy(g => g.ZoneCode, StringComparer.Ordinal).ThenBy(g => g.RaftCode, StringComparer.Ordinal).ThenBy(g => g.CycleStartDate)
            .ToList();
        var all = rows.Select(r => r.View).ToList();
        return new HarvestYieldResult(groups, new HarvestYieldTotals(all.Count, all.Sum(l => l.EstimatedKg), all.Sum(l => l.SoldKg), all.Sum(l => l.LostKg), all.Sum(l => l.DifferenceKg), all.Any(l => l.NotFinal), all.Sum(l => l.CageCount)));
    }

    private static HarvestYieldLot LotOf(HarvestLot lot, LotSalesFigures sales, DateOnly from, DateOnly to, Func<int, string> statusName)
    {
        var figures = HarvestLotFigures.Of(lot, sales);
        return new HarvestYieldLot(
            lot.Id, lot.LotCode, lot.HarvestDate, lot.HarvestDate >= from && lot.HarvestDate <= to, lot.RaftId, lot.Raft.Code, lot.Raft.ZoneId, lot.Raft.Zone.Code, lot.CycleId,
            lot.StatusId, statusName(lot.StatusId), lot.EstimatedKg, figures.InvoicedKg, figures.ReturnedKg, figures.SoldKg, figures.LostKg, figures.DifferenceKg, figures.NotFinal, lot.CageCount, figures.RemainingKg);
    }

    private static Guid KeyOf(HarvestLot lot, HarvestYieldGrouping groupBy) => groupBy switch
    {
        HarvestYieldGrouping.Zone => lot.Raft.ZoneId,
        HarvestYieldGrouping.Cycle => lot.CycleId,
        _ => lot.RaftId,
    };

    private static HarvestYieldGroup GroupOf(
        HarvestLot first, IReadOnlyList<HarvestYieldLot> lots, HarvestYieldGrouping groupBy, Func<int, string> statusName, IReadOnlyList<CultivationCycle> cycles,
        IReadOnlyDictionary<Guid, YieldCycleFigures>? cycleFigures)
    {
        var (raft, zone, cycle) = (first.Raft, first.Raft.Zone, first.Cycle);
        var byZone = groupBy == HarvestYieldGrouping.Zone;
        var byCycle = groupBy == HarvestYieldGrouping.Cycle;
        var label = groupBy switch
        {
            HarvestYieldGrouping.Zone => $"{zone.Code} - {zone.Name}",
            HarvestYieldGrouping.Cycle => $"{raft.Code} from {cycle.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            _ => raft.Code,
        };
        // The plan figures are those of the distinct cycles of the group (PM decision 11/11/2026): the target is null when none of them has one.
        var withTarget = cycles.Where(c => c.TargetYieldKg is > 0).ToList();
        decimal? target = withTarget.Count == 0 ? null : withTarget.Sum(c => c.TargetYieldKg!.Value);
        var seeds = cycles.Select(c => cycleFigures?.GetValueOrDefault(c.Id)?.Seed).OfType<CycleSeedTotal>().ToList();
        var units = seeds.Select(s => s.Unit).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToList();
        // The nursery of the cycles of the group (estimates, counted by eye): the pieces put in, the survivors and the loss of each batch shared by what each cycle seeded (v2); null when no cycle came from a nursery.
        var nurseries = cycles.Select(c => cycleFigures?.GetValueOrDefault(c.Id)?.Nursery).OfType<NurseryCycleFacts>().Select(NurseryRules.SummaryOf).ToList();
        var (nurseryIn, nurserySurvivors, nurseryLoss) = (nurseries.Sum(n => n.QuantityIn), nurseries.Sum(n => n.Survivors), nurseries.Sum(n => n.LossPieces));
        return new HarvestYieldGroup(
            KeyOf(first, groupBy),
            label,
            zone.Id,
            zone.Code,
            zone.Name,
            byZone ? null : raft.Id,
            byZone ? null : raft.Code,
            byCycle ? cycle.Id : null,
            byCycle ? cycle.StartDate : null,
            byCycle ? cycle.CloseDate : null,
            byCycle ? statusName(cycle.StatusId) : null,
            lots.Count,
            lots.Sum(l => l.EstimatedKg),
            lots.Sum(l => l.SoldKg),
            lots.Sum(l => l.LostKg),
            lots.Sum(l => l.DifferenceKg),
            lots.Any(l => l.NotFinal),
            lots,
            lots.Sum(l => l.CageCount),
            lots.Sum(l => l.EstimatedKg),
            target,
            // Remaining against the target of the whole cycles (every lot of them), not only the lots of the period.
            target is { } t && cycleFigures is not null ? t - cycles.Sum(c => cycleFigures.TryGetValue(c.Id, out var f) ? f.HarvestedKg : 0m) : null,
            seeds.Count == 0 ? null : seeds.Sum(s => s.Quantity),
            units.Count == 1 ? units[0] : null,
            // Only lots still for sale have kg left to sell.
            lots.Where(l => l.NotFinal).Sum(l => l.RemainingKg),
            nurseries.Count == 0 ? null : nurseryIn,
            nurseries.Count == 0 ? null : nurserySurvivors,
            nurseries.Count == 0 ? null : nurseryLoss,
            nurseries.Count == 0 ? null : (nurseryIn > 0 ? decimal.Round(nurseryLoss / nurseryIn * 100, 1) : 0m));
    }
}
