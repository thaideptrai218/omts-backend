using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Labor.Payrolls;
using OMTS.Application.Features.Labor.Work;
using OMTS.Application.Features.WorkDayPlans;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Costs;

/// <summary>The labour cost of one raft on one day; <see cref="RaftId"/> is null for work at zone level (nursery), which is attributed to the zone.</summary>
public sealed record RaftDayCost(Guid? RaftId, Guid ZoneId, DateOnly Date, decimal Amount);

/// <summary>
/// The same cost with who and what it was for: one employee (of the given type that day), one job type, the way it was paid, their paid minutes, the shifts counted and the pay on one raft and day
/// (BR-LABOR-07: hours and split by job type, by employee, by employee type and by pay kind).
/// </summary>
public sealed record LaborCostPiece(
    Guid? RaftId, Guid ZoneId, DateOnly Date, Guid EmployeeId, Guid JobTypeId, int Minutes, decimal Amount, PayLineKind Kind = PayLineKind.Hourly, decimal Shifts = 0,
    EmployeeType EmployeeType = EmployeeType.SeasonalWorker);

/// <summary>
/// BR-LABOR-07: what the records pay for work on rafts, by the same rules as <see cref="PayrollCalculator"/> (the salary setup in force on the day): a worker paid per shift costs the shifts times the
/// shift rate, one paid by the hour the hours times the hourly rate, and a fixed-salary employee with field shifts the shifts of the days with a work assignment. The pay of a job type is
/// worked out and rounded by <see cref="PayrollCalculator"/>, then shared between the job periods of that type by their minutes, each share rounded to the VND and the remainder going to the
/// last period, so the shares always add up to the payslip's pay. The fixed monthly salary itself belongs to no raft and is not in the cost. Incomplete days and work with no rate cost
/// nothing (BR-LABOR-06, BR-LABOR-12). Bonuses and deductions of a payslip belong to the employee, not to a raft, and are not in the cost.
/// </summary>
public static class LaborCostCalculator
{
    public static IReadOnlyList<RaftDayCost> Calculate(DateOnly from, DateOnly to, PayrollWorkData data) =>
        CalculatePieces(from, to, data).GroupBy(p => (p.RaftId, p.ZoneId, p.Date))
            .Select(g => new RaftDayCost(g.Key.RaftId, g.Key.ZoneId, g.Key.Date, g.Sum(p => p.Amount))).OrderBy(c => c.Date).ToList();

    /// <summary>The "employee" an outsider line is booked under, so it shares the pieces, the split by job type and the totals of the cost.</summary>
    public static readonly Guid OutsiderEmployeeId = Guid.Empty;

    /// <summary>
    /// The people hired outside for the day of a work-day plan (the plans that are not cancelled): People times the rate on the raft of the plan, or on its zone for a nursery plan, as shifts of a seasonal worker
    /// of the job type of the plan's kind. They are outside payroll and cost their agreed pay from the day they are hired, paid or not.
    /// </summary>
    public static IReadOnlyList<LaborCostPiece> OutsiderPieces(IEnumerable<PlanOutsiderHire> hires, IReadOnlyDictionary<Guid, JobType> jobTypes) =>
        hires.Where(h => h.Plan.Status != WorkDayPlanStatus.Cancelled && h.Amount > 0)
            .Select(h => new LaborCostPiece(
                h.Plan.RaftId, h.Plan.ZoneId, h.Plan.WorkDate, OutsiderEmployeeId,
                jobTypes.Values.FirstOrDefault(j => string.Equals(j.Name, WorkDayPlanRules.JobNameOf(h.Plan.Kind), StringComparison.OrdinalIgnoreCase))?.Id ?? Guid.Empty,
                0, h.Amount, PayLineKind.Shift, h.People, EmployeeType.SeasonalWorker)).ToList();

    public static IReadOnlyList<LaborCostPiece> CalculatePieces(DateOnly from, DateOnly to, PayrollWorkData data)
    {
        var pieceCosts = new Dictionary<(Guid? RaftId, Guid ZoneId, DateOnly Date, Guid EmployeeId, Guid JobTypeId, PayLineKind Kind), (int Minutes, decimal Amount, decimal Shifts, EmployeeType Type)>();
        foreach (var employee in data.Employees)
        {
            var figures = PayrollCalculator.Calculate(employee, from, to, PayrollPeriodType.Custom, data);
            foreach (var group in figures.Days.GroupBy(d => (d.JobTypeId, d.Kind, d.Rate)))
            {
                // The payslip rounds once per job type, kind and rate over the whole range; that amount is what is shared, so a sum of per-day roundings never drifts from the sheet.
                var pay = figures.Jobs.First(j => j.JobTypeId == group.Key.JobTypeId && j.Kind == group.Key.Kind && j.Rate == group.Key.Rate).Amount;
                var shares = group.OrderBy(d => d.Date)
                    .SelectMany(d => d.Shares.Where(s => s.Minutes > 0).Select(s => (Day: d, Share: s))).ToList();
                var given = 0m;
                var minutes = shares.Sum(s => s.Share.Minutes);
                for (var i = 0; i < shares.Count; i++)
                {
                    var (day, share) = shares[i];
                    var amount = i == shares.Count - 1 ? pay - given : Math.Round(pay * share.Minutes / minutes, 0, MidpointRounding.AwayFromZero);
                    given += amount;
                    var shifts = day.Minutes > 0 ? day.Shifts * share.Minutes / day.Minutes : 0;
                    var key = (share.RaftId, share.ZoneId, day.Date, employee.Id, day.JobTypeId, day.Kind);
                    var (m, a, s, _) = pieceCosts.GetValueOrDefault(key);
                    pieceCosts[key] = (m + share.Minutes, a + amount, s + shifts, WorkRules.TypeOn(employee, day.Date));
                }
            }
        }

        return pieceCosts.Select(c => new LaborCostPiece(
            c.Key.RaftId, c.Key.ZoneId, c.Key.Date, c.Key.EmployeeId, c.Key.JobTypeId, c.Value.Minutes, c.Value.Amount, c.Key.Kind, c.Value.Shifts, c.Value.Type)).ToList();
    }
}
