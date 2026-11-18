using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.Labor.Costs;

/// <summary>
/// The labour cost by raft and by cultivation cycle (BR-LABOR-07), worked out from the attendance and job periods whenever it is read, so it
/// follows the records until they are locked by a Finalized payroll. The Owner only (the API checks the role): it is made of the pay of the workers.
/// </summary>
public sealed class LaborCostService(
    IPayrollWorkReader workReader,
    ICultivationCycleRepository cycles,
    IRaftRepository rafts,
    IFarmingZoneRepository zones,
    TimeProvider timeProvider)
{
    private const int MaxDays = 366;

    public async Task<Result<LaborCostByRaft>> ByRaftAsync(LaborCostQuery query, CancellationToken cancellationToken)
    {
        if (query.From > query.To || query.To.DayNumber - query.From.DayNumber >= MaxDays)
        {
            return AppError.Invalid("Period");
        }

        var costs = await CostsAsync(query.From, query.To, cancellationToken);
        if (query.ZoneId is { } zoneId)
        {
            costs = costs.Where(c => c.ZoneId == zoneId).ToList();
        }

        var totals = costs.GroupBy(c => (c.RaftId, c.ZoneId)).Select(g => (g.Key.RaftId, g.Key.ZoneId, Amount: g.Sum(c => c.Amount))).ToList();
        var raftCodes = (await rafts.GetByIdsAsync(totals.Where(t => t.RaftId is not null).Select(t => t.RaftId!.Value).ToList(), cancellationToken))
            .ToDictionary(r => r.Id, r => r.Code);
        var zoneNames = (await zones.GetByIdsAsync(totals.Select(t => t.ZoneId).Distinct().ToList(), cancellationToken))
            .ToDictionary(z => z.Id, z => z.Name);
        var items = totals
            .Select(t => new RaftLaborCost(
                t.RaftId, t.RaftId is { } raftId ? raftCodes.GetValueOrDefault(raftId) : null, t.ZoneId, zoneNames.GetValueOrDefault(t.ZoneId, string.Empty), t.Amount))
            .OrderBy(i => i.ZoneName, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.RaftCode, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new LaborCostByRaft(query.From, query.To, items.Sum(i => i.Amount), items);
    }

    public async Task<Result<CycleLaborCost>> OfCycleAsync(Guid cycleId, CancellationToken cancellationToken)
    {
        var cycle = await cycles.GetAsync(cycleId, cancellationToken);
        if (cycle is null)
        {
            return AppError.NotFound;
        }

        var last = cycle.CloseDate ?? timeProvider.Today();
        var days = cycle.StartDate > last
            ? []
            : (await CostsAsync(cycle.StartDate, last, cancellationToken)).Where(c => c.RaftId == cycle.RaftId)
                .GroupBy(c => c.Date).Select(g => new DayLaborCost(g.Key, g.Sum(c => c.Amount))).OrderBy(d => d.Date).ToList();
        var raft = (await rafts.GetByIdsAsync([cycle.RaftId], cancellationToken)).FirstOrDefault();
        return new CycleLaborCost(cycle.Id, cycle.RaftId, raft?.Code ?? string.Empty, cycle.StartDate, cycle.CloseDate, days.Sum(d => d.Amount), days);
    }

    private async Task<IReadOnlyList<RaftDayCost>> CostsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        LaborCostCalculator.Calculate(from, to, await workReader.LoadAsync(from, to, null, cancellationToken));
}
