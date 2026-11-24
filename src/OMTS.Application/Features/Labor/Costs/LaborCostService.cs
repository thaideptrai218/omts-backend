using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Labor.Costs;

/// <summary>
/// The labour cost by raft and by cultivation cycle (BR-LABOR-07), worked out from the attendance and job periods whenever it is read, so it
/// follows the records until they are locked by a Finalized payroll. Owner and Accountant (PAYROLL View, checked by the API): it is made of the pay of the workers.
/// Besides the money it gives the paid hours and the split by job type, and for a cycle the split by employee.
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

        var (pieces, jobNames, _) = await PiecesAsync(query.From, query.To, cancellationToken);
        if (query.ZoneId is { } zoneId)
        {
            pieces = pieces.Where(c => c.ZoneId == zoneId).ToList();
        }

        var totals = pieces.GroupBy(c => (c.RaftId, c.ZoneId)).Select(g => (g.Key.RaftId, g.Key.ZoneId, Pieces: g.ToList())).ToList();
        var raftCodes = (await rafts.GetByIdsAsync(totals.Where(t => t.RaftId is not null).Select(t => t.RaftId!.Value).ToList(), cancellationToken))
            .ToDictionary(r => r.Id, r => r.Code);
        var zoneNames = (await zones.GetByIdsAsync(totals.Select(t => t.ZoneId).Distinct().ToList(), cancellationToken))
            .ToDictionary(z => z.Id, z => z.Name);
        var items = totals
            .Select(t => new RaftLaborCost(
                t.RaftId, t.RaftId is { } raftId ? raftCodes.GetValueOrDefault(raftId) : null, t.ZoneId, zoneNames.GetValueOrDefault(t.ZoneId, string.Empty),
                t.Pieces.Sum(p => p.Amount), HoursOf(t.Pieces), ByJob(t.Pieces, jobNames)))
            .OrderBy(i => i.ZoneName, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.RaftCode, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new LaborCostByRaft(query.From, query.To, items.Sum(i => i.Amount), items, items.Sum(i => i.Hours));
    }

    public async Task<Result<CycleLaborCost>> OfCycleAsync(Guid cycleId, CancellationToken cancellationToken)
    {
        var cycle = await cycles.GetAsync(cycleId, cancellationToken);
        if (cycle is null)
        {
            return AppError.NotFound;
        }

        var raft = (await rafts.GetByIdsAsync([cycle.RaftId], cancellationToken)).FirstOrDefault();
        var last = cycle.CloseDate ?? timeProvider.Today();
        if (cycle.StartDate > last)
        {
            return new CycleLaborCost(cycle.Id, cycle.RaftId, raft?.Code ?? string.Empty, cycle.StartDate, cycle.CloseDate, 0, [], 0, [], []);
        }

        var (all, jobNames, employeeNames) = await PiecesAsync(cycle.StartDate, last, cancellationToken);
        return Cycle(cycle, raft?.Code ?? string.Empty, all, jobNames, employeeNames);
    }

    /// <summary>
    /// Every cycle of a raft with its cost, hours and split, the newest first, from one read of the work: the list the cost screen opens under a raft. It needs only PAYROLL View, so the Accountant
    /// does not need the cultivation journey rights that the cycle list of the farming module asks for.
    /// </summary>
    public async Task<Result<IReadOnlyList<CycleLaborCost>>> OfRaftCyclesAsync(Guid raftId, CancellationToken cancellationToken)
    {
        var raft = (await rafts.GetByIdsAsync([raftId], cancellationToken)).FirstOrDefault();
        if (raft is null)
        {
            return AppError.NotFound;
        }

        var list = (await cycles.ListByRaftAsync(raftId, cancellationToken)).Where(c => c.StartDate <= (c.CloseDate ?? timeProvider.Today())).ToList();
        if (list.Count == 0)
        {
            return Result<IReadOnlyList<CycleLaborCost>>.Success([]);
        }

        var today = timeProvider.Today();
        var (all, jobNames, employeeNames) = await PiecesAsync(list.Min(c => c.StartDate), list.Max(c => c.CloseDate ?? today), cancellationToken);
        return Result<IReadOnlyList<CycleLaborCost>>.Success(
            list.Select(c => Cycle(c, raft.Code, all.Where(p => p.Date >= c.StartDate && p.Date <= (c.CloseDate ?? today)).ToList(), jobNames, employeeNames))
                .OrderByDescending(c => c.StartDate).ToList());
    }

    private static CycleLaborCost Cycle(
        CultivationCycle cycle, string raftCode, IReadOnlyList<LaborCostPiece> all, IReadOnlyDictionary<Guid, string> jobNames,
        IReadOnlyDictionary<Guid, (string? Code, string Name)> employeeNames)
    {
        var pieces = all.Where(c => c.RaftId == cycle.RaftId).ToList();
        var days = pieces.GroupBy(c => c.Date).Select(g => new DayLaborCost(g.Key, g.Sum(c => c.Amount))).OrderBy(d => d.Date).ToList();
        var employees = pieces.GroupBy(c => c.EmployeeId).Select(g =>
        {
            var (code, name) = employeeNames.GetValueOrDefault(g.Key, (null, string.Empty));
            return new EmployeeLaborCost(g.Key, code, name, HoursOf(g), g.Sum(c => c.Amount), ByJob(g, jobNames));
        }).OrderByDescending(e => e.Amount).ThenBy(e => e.EmployeeName, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new CycleLaborCost(
            cycle.Id, cycle.RaftId, raftCode, cycle.StartDate, cycle.CloseDate, days.Sum(d => d.Amount), days, HoursOf(pieces), ByJob(pieces, jobNames), employees);
    }
    private static decimal HoursOf(IEnumerable<LaborCostPiece> pieces) => Math.Round(pieces.Sum(p => p.Minutes) / 60m, 2, MidpointRounding.AwayFromZero);

    private static IReadOnlyList<JobLaborCost> ByJob(IEnumerable<LaborCostPiece> pieces, IReadOnlyDictionary<Guid, string> jobNames) =>
        pieces.GroupBy(p => p.JobTypeId).Select(g => new JobLaborCost(g.Key, jobNames.GetValueOrDefault(g.Key, string.Empty), HoursOf(g), g.Sum(p => p.Amount)))
            .OrderByDescending(j => j.Amount).ThenBy(j => j.JobName, StringComparer.CurrentCultureIgnoreCase).ToList();

    private async Task<(IReadOnlyList<LaborCostPiece> Pieces, IReadOnlyDictionary<Guid, string> JobNames, IReadOnlyDictionary<Guid, (string? Code, string Name)> Employees)> PiecesAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var data = await workReader.LoadAsync(from, to, null, cancellationToken);
        return (
            LaborCostCalculator.CalculatePieces(from, to, data),
            data.JobTypes.ToDictionary(j => j.Key, j => j.Value.Name),
            data.Employees.ToDictionary(e => e.Id, e => (e.Code, e.FullName)));
    }
}