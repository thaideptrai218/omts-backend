using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Labor.Work;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Costs;

/// <summary>The labour cost of one raft on one day; <see cref="RaftId"/> is null for work at zone level (nursery), which is attributed to the zone.</summary>
public sealed record RaftDayCost(Guid? RaftId, Guid ZoneId, DateOnly Date, decimal Amount);

/// <summary>
/// BR-LABOR-07: the pay of a day is shared between the rafts the employee worked on. For each seasonal worker and day with a complete attendance
/// record, the pay of each job type (its hours times the hourly rate in force that day, to the nearest VND, as in <see cref="Payrolls.PayrollCalculator"/>)
/// is split across the job periods of that type by their minutes, each share rounded to the VND and the remainder going to the last period, so the
/// shares always add up to the day's pay. Staff days, incomplete days and hours with no rate cost nothing (BR-LABOR-09, BR-LABOR-06, BR-LABOR-12).
/// Bonuses and deductions of a payslip belong to the employee, not to a raft, and are not in the cost.
/// </summary>
public static class LaborCostCalculator
{
    public static IReadOnlyList<RaftDayCost> Calculate(DateOnly from, DateOnly to, PayrollWorkData data)
    {
        var costs = new Dictionary<(Guid? RaftId, Guid ZoneId, DateOnly Date), decimal>();
        var employees = data.Employees.ToDictionary(e => e.Id);
        var periodsOf = data.Assignments.ToLookup(a => (a.EmployeeId, a.Date));
        foreach (var record in data.Attendances.Where(a => a.Date >= from && a.Date <= to && a.IsComplete))
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

                var pay = Math.Round(job.Minutes * rate.HourlyRate / 60m, 0, MidpointRounding.AwayFromZero);
                foreach (var (period, share) in Split(pay, day.Periods.Where(p => p.JobTypeId == job.JobTypeId && p.Minutes > 0).ToList()))
                {
                    var key = (period.RaftId, period.ZoneId, record.Date);
                    costs[key] = costs.GetValueOrDefault(key) + share;
                }
            }
        }

        return costs.Select(c => new RaftDayCost(c.Key.RaftId, c.Key.ZoneId, c.Key.Date, c.Value)).OrderBy(c => c.Date).ToList();
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
