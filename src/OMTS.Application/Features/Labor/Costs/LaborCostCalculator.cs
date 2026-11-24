using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Labor.Work;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Costs;

/// <summary>The labour cost of one raft on one day; <see cref="RaftId"/> is null for work at zone level (nursery), which is attributed to the zone.</summary>
public sealed record RaftDayCost(Guid? RaftId, Guid ZoneId, DateOnly Date, decimal Amount);

/// <summary>The same cost with who and what it was for: one employee, one job type, their paid minutes and the pay on one raft and day (BR-LABOR-07: hours and split by job type and by employee).</summary>
public sealed record LaborCostPiece(Guid? RaftId, Guid ZoneId, DateOnly Date, Guid EmployeeId, Guid JobTypeId, int Minutes, decimal Amount);

/// <summary>
/// BR-LABOR-07: the pay of a day is shared between the rafts the employee worked on. For each seasonal worker and day with a complete attendance
/// record, the pay of each job type (its hours times the hourly rate in force that day, to the nearest VND, as in <see cref="Payrolls.PayrollCalculator"/>)
/// is split across the job periods of that type by their minutes, each share rounded to the VND and the remainder going to the last period, so the
/// shares always add up to the day's pay. Staff days, incomplete days and hours with no rate cost nothing (BR-LABOR-09, BR-LABOR-06, BR-LABOR-12).
/// Bonuses and deductions of a payslip belong to the employee, not to a raft, and are not in the cost.
/// </summary>
public static class LaborCostCalculator
{
    public static IReadOnlyList<RaftDayCost> Calculate(DateOnly from, DateOnly to, PayrollWorkData data) =>
        CalculatePieces(from, to, data).GroupBy(p => (p.RaftId, p.ZoneId, p.Date))
            .Select(g => new RaftDayCost(g.Key.RaftId, g.Key.ZoneId, g.Key.Date, g.Sum(p => p.Amount))).OrderBy(c => c.Date).ToList();

    public static IReadOnlyList<LaborCostPiece> CalculatePieces(DateOnly from, DateOnly to, PayrollWorkData data)
    {
        var pieceCosts = new Dictionary<(Guid? RaftId, Guid ZoneId, DateOnly Date, Guid EmployeeId, Guid JobTypeId), (int Minutes, decimal Amount)>();
        // L12: the pay is rounded once per employee, job type and rate over the whole range, as PayrollCalculator does, then shared between the periods; a sum of per-day roundings could differ from the payroll by a few dong.
        var pieces = new Dictionary<(Guid EmployeeId, Guid JobTypeId, decimal Rate), List<(WorkedPeriod Period, DateOnly Date)>>();
        var employees = data.Employees.ToDictionary(e => e.Id);
        var periodsOf = data.Assignments.ToLookup(a => (a.EmployeeId, a.Date));
        foreach (var record in data.Attendances.Where(a => a.Date >= from && a.Date <= to && a.IsComplete).OrderBy(a => a.Date))
        {
            if (!employees.TryGetValue(record.EmployeeId, out var employee)
                || WorkRules.TypeOn(employee, record.Date) != EmployeeType.SeasonalWorker)
            {
                continue;
            }

            var day = WorkHoursCalculator.Calculate(
                record.CheckInAt, record.CheckOutAt, periodsOf[(record.EmployeeId, record.Date)], AttendanceSettingsVersion.EffectiveOn(data.Settings, record.Date));
            foreach (var job in day.Jobs.Where(j => j.Minutes > 0))
            {
                var rate = data.JobTypes.TryGetValue(job.JobTypeId, out var jobType) ? PayRate.EffectiveOn(jobType.Rates, record.Date) : null;
                if (rate is null)
                {
                    continue;
                }

                var key = (record.EmployeeId, job.JobTypeId, rate.HourlyRate);
                if (!pieces.TryGetValue(key, out var list))
                {
                    pieces[key] = list = [];
                }

                // The job's rounded minutes are shared between its periods of the day by their own minutes, so the periods keep their raft.
                var shares = day.Periods.Where(p => p.JobTypeId == job.JobTypeId && p.Minutes > 0).ToList();
                var rawTotal = shares.Sum(p => p.Minutes);
                var given = 0;
                for (var i = 0; i < shares.Count; i++)
                {
                    var minutes = i == shares.Count - 1 ? job.Minutes - given : (int)Math.Floor((decimal)job.Minutes * shares[i].Minutes / rawTotal);
                    given += minutes;
                    list.Add((shares[i] with { Minutes = minutes }, record.Date));
                }
            }
        }

        foreach (var ((employeeId, jobTypeId, rate), list) in pieces)
        {
            var pay = Math.Round(list.Sum(p => p.Period.Minutes) * rate / 60m, 0, MidpointRounding.AwayFromZero);
            var split = Split(pay, list.Select(p => p.Period).Where(p => p.Minutes > 0).ToList()).ToList();
            var withMinutes = list.Where(p => p.Period.Minutes > 0).ToList();
            for (var i = 0; i < split.Count; i++)
            {
                var key = (split[i].Period.RaftId, split[i].Period.ZoneId, withMinutes[i].Date, employeeId, jobTypeId);
                var (minutes, amount) = pieceCosts.GetValueOrDefault(key);
                pieceCosts[key] = (minutes + split[i].Period.Minutes, amount + split[i].Share);
            }
        }

        return pieceCosts.Select(c => new LaborCostPiece(c.Key.RaftId, c.Key.ZoneId, c.Key.Date, c.Key.EmployeeId, c.Key.JobTypeId, c.Value.Minutes, c.Value.Amount)).ToList();
    }

    private static IEnumerable<(WorkedPeriod Period, decimal Share)> Split(decimal pay, IReadOnlyList<WorkedPeriod> periods)
    {
        var minutes = periods.Sum(p => p.Minutes);
        var given = 0m;
        for (var i = 0; i < periods.Count; i++)
        {
            var share = i == periods.Count - 1
                ? pay - given
                : Math.Round(pay * periods[i].Minutes / minutes, 0, MidpointRounding.AwayFromZero);
            given += share;
            yield return (periods[i], share);
        }
    }
}
