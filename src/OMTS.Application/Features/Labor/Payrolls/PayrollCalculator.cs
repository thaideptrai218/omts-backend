using System.Globalization;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Labor.Work;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Payrolls;

/// <summary>One job type at one hourly rate in a payslip: its minutes and the pay they give.</summary>
public sealed record SlipJobFigure(Guid JobTypeId, string JobName, decimal HourlyRate, int Minutes, decimal Amount)
{
    public decimal Hours => PayrollCalculator.HoursOf(Minutes);
}

/// <summary>What the records give for one employee in a period (BR-LABOR-10).</summary>
/// <param name="IncompleteDays">Days of a seasonal worker with only a check-in or only a check-out: they count no hours (BR-LABOR-06).</param>
/// <param name="UnratedDays">Days and job types whose hours have no rate in force on that day (BR-LABOR-12).</param>
public sealed record SlipFigures(
    Employee Employee, IReadOnlyList<SlipJobFigure> Jobs, IReadOnlyList<DateOnly> IncompleteDays, IReadOnlyList<(DateOnly Day, string JobName)> UnratedDays)
{
    public int Minutes => Jobs.Sum(j => j.Minutes);
    public decimal Hours => PayrollCalculator.HoursOf(Minutes);
    public decimal MainPay => Jobs.Sum(j => j.Amount);

    /// <summary>The rates in force in the period as the screen writes them, e.g. "30.000 / 32.000"; null with no hours.</summary>
    public string? RateNote => Jobs.Count == 0
        ? null
        : string.Join(" / ", Jobs.Select(j => j.HourlyRate).Distinct().Order().Select(PayrollCalculator.FormatRate));
}

/// <summary>
/// The pay of the level of payroll the Owner chose: for each day of a seasonal worker with a complete attendance record, the hours of each
/// job type (BR-LABOR-05, by <see cref="WorkHoursCalculator"/>) times the hourly rate in force on that day (BR-LABOR-12), summed by job
/// type and rate and rounded to the VND; no overtime, commission, insurance or tax. The days an employee is Staff count nothing: Staff
/// employees are not paid through OMTS (BR-LABOR-09).
/// </summary>
public static class PayrollCalculator
{
    public static decimal HoursOf(int minutes) => Math.Round(minutes / 60m, 2, MidpointRounding.AwayFromZero);

    public static string FormatRate(decimal rate) => ((long)rate).ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.');

    public static SlipFigures Calculate(Employee employee, DateOnly from, DateOnly to, PayrollWorkData data)
    {
        var periodsOf = data.Assignments.Where(a => a.EmployeeId == employee.Id).ToLookup(a => a.Date);
        var minutes = new Dictionary<(Guid JobTypeId, decimal Rate), int>();
        var incomplete = new List<DateOnly>();
        var unrated = new List<(DateOnly, string)>();
        foreach (var record in data.Attendances.Where(a => a.EmployeeId == employee.Id && a.Date >= from && a.Date <= to).OrderBy(a => a.Date))
        {
            if (WorkRules.TypeOn(employee, record.Date) != EmployeeType.SeasonalWorker)
            {
                continue;
            }

            if (!record.IsComplete)
            {
                incomplete.Add(record.Date);
                continue;
            }

            var day = WorkHoursCalculator.Calculate(
                record.CheckInAt, record.CheckOutAt, periodsOf[record.Date], AttendanceSettingsVersion.EffectiveOn(data.Settings, record.Date));
            foreach (var job in day.Jobs.Where(j => j.Minutes > 0))
            {
                var jobType = data.JobTypes.GetValueOrDefault(job.JobTypeId);
                var rate = jobType is null ? null : PayRate.EffectiveOn(jobType.Rates, record.Date);
                if (rate is null)
                {
                    unrated.Add((record.Date, jobType?.Name ?? string.Empty));
                    continue;
                }

                var key = (job.JobTypeId, rate.HourlyRate);
                minutes[key] = minutes.GetValueOrDefault(key) + job.Minutes;
            }
        }

        var jobs = minutes
            .Select(m => new SlipJobFigure(
                m.Key.JobTypeId, data.JobTypes[m.Key.JobTypeId].Name, m.Key.Rate, m.Value,
                Math.Round(m.Value * m.Key.Rate / 60m, 0, MidpointRounding.AwayFromZero)))
            .OrderBy(j => j.JobName, StringComparer.CurrentCultureIgnoreCase).ThenBy(j => j.HourlyRate)
            .ToList();
        return new SlipFigures(employee, jobs, incomplete, unrated);
    }
}
