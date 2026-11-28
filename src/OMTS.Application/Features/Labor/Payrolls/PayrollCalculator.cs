using System.Globalization;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Labor.Work;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Payrolls;

/// <summary>One job type on one day as it was paid: its minutes, the shifts counted (0 for hourly pay), the rate in force and the amount to the VND.</summary>
public sealed record SlipDay(DateOnly Date, Guid JobTypeId, string JobName, PayLineKind Kind, int Minutes, decimal Shifts, decimal Rate, decimal Amount);

/// <summary>A day and job type whose work has no rate in force on that day: an hourly rate for <see cref="PayLineKind.Hourly"/>, a shift rate for the shift kinds.</summary>
public sealed record UnratedDay(DateOnly Day, string JobName, PayLineKind Kind);

/// <summary>One job type at one rate in a payslip: its minutes and shifts and the pay they give.</summary>
public sealed record SlipJobFigure(Guid JobTypeId, string JobName, PayLineKind Kind, decimal Rate, int Minutes, decimal Shifts, decimal Amount)
{
    public decimal Hours => PayrollCalculator.HoursOf(Minutes);
    public decimal HourlyRate => Kind == PayLineKind.Hourly ? Rate : 0;
    public decimal ShiftRate => Kind == PayLineKind.Hourly ? 0 : Rate;
}

/// <summary>What the records give for one employee in a period (BR-LABOR-10).</summary>
/// <param name="IncompleteDays">Days with only a check-in or only a check-out of an employee paid by the hour or the shift: they count no hours (BR-LABOR-06).</param>
/// <param name="UnratedDays">Days and job types whose work has no rate in force on that day (BR-LABOR-12).</param>
public sealed record SlipFigures(
    Employee Employee, IReadOnlyList<SlipJobFigure> Jobs, IReadOnlyList<DateOnly> IncompleteDays, IReadOnlyList<UnratedDay> UnratedDays)
{
    /// <summary>The day by day pay behind <see cref="Jobs"/>.</summary>
    public IReadOnlyList<SlipDay> Days { get; init; } = [];

    /// <summary>The fixed monthly amount, in full on every sheet whatever its period; 0 for the other salary types.</summary>
    public decimal FixedPay { get; init; }

    /// <summary>The salary type in force on the last day of the period; null for a Staff employee with no setup.</summary>
    public SalaryType? SalaryType { get; init; }

    /// <summary>A Staff employee with no setup in the period: nothing is worked out and the sheet cannot be finalized.</summary>
    public bool MissingSetup { get; init; }

    public int Minutes => Jobs.Sum(j => j.Minutes);
    public decimal Hours => PayrollCalculator.HoursOf(Minutes);
    public decimal Shifts => Jobs.Sum(j => j.Shifts);

    /// <summary>The fixed salary, the hourly pay and the per-shift pay.</summary>
    public decimal MainPay => FixedPay + Jobs.Where(j => j.Kind != PayLineKind.FieldShift).Sum(j => j.Amount);

    /// <summary>The shifts of field work of a fixed-salary employee.</summary>
    public decimal ShiftPay => Jobs.Where(j => j.Kind == PayLineKind.FieldShift).Sum(j => j.Amount);

    /// <summary>The employee has a payslip on a sheet: something is paid, or the missing setup must be shown.</summary>
    public bool OnSheet => Jobs.Count > 0 || FixedPay > 0 || MissingSetup;

    /// <summary>The rates in force as the screen writes them, e.g. "30.000 / 300.000 đ/ca"; null with nothing paid by a rate.</summary>
    public string? RateNote => Jobs.Count == 0
        ? null
        : string.Join(
            " / ",
            Jobs.Where(j => j.Kind == PayLineKind.Hourly).Select(j => j.Rate).Distinct().Order().Select(PayrollCalculator.FormatRate)
                .Concat(Jobs.Where(j => j.Kind != PayLineKind.Hourly).Select(j => j.Rate).Distinct().Order().Select(r => $"{PayrollCalculator.FormatRate(r)} đ/ca")));
}

/// <summary>
/// The pay of the level of payroll the Owner chose, for each employee and each day with a complete attendance record, by the salary setup in force on that day
/// (<see cref="SalaryPlan.TypeOn"/>, the default of the employee type when there is none):
/// <list type="bullet">
/// <item><b>PerShift</b>: the shifts of each job type (<see cref="ShiftCounter"/>) times the shift rate in force on that day; the pay is the main pay.</item>
/// <item><b>Hourly</b>: the hours of each job type (BR-LABOR-05, <see cref="WorkHoursCalculator"/>) times the hourly rate in force on that day (BR-LABOR-12).</item>
/// <item><b>FixedMonthly</b>: the monthly amount in full once on every sheet, Monthly or Custom (no pro rata, from the setup in force on its last day).
/// With <see cref="EmployeeSalarySetup.PayFieldShifts"/> the shifts of the days that have a work assignment are paid at the shift rate as the shift pay; ordinary attendance with no assignment earns nothing.</item>
/// <item>No setup (Staff): nothing is worked out and <see cref="SlipFigures.MissingSetup"/> says so.</item>
/// </list>
/// The pay is summed by job type and rate and rounded to the VND; no overtime, commission, insurance or tax.
/// </summary>
public static class PayrollCalculator
{
    public static decimal HoursOf(int minutes) => Math.Round(minutes / 60m, 2, MidpointRounding.AwayFromZero);

    public static string FormatRate(decimal rate) => ((long)rate).ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.');

    public static SlipFigures Calculate(Employee employee, DateOnly from, DateOnly to, PayrollPeriodType periodType, PayrollWorkData data)
    {
        var periodsOf = data.Assignments.Where(a => a.EmployeeId == employee.Id).ToLookup(a => a.Date);
        var days = new List<SlipDay>();
        var incomplete = new List<DateOnly>();
        var unrated = new List<UnratedDay>();
        foreach (var record in data.Attendances.Where(a => a.EmployeeId == employee.Id && a.Date >= from && a.Date <= to).OrderBy(a => a.Date))
        {
            var setup = SalaryPlan.PayingSetupOn(employee, record.Date);
            var kind = (setup?.Type ?? EmployeeSalarySetup.DefaultFor(WorkRules.TypeOn(employee, record.Date))) switch
            {
                SalaryType.PerShift => PayLineKind.Shift,
                SalaryType.Hourly => PayLineKind.Hourly,
                SalaryType.FixedMonthly when setup is { PayFieldShifts: true } => PayLineKind.FieldShift,
                _ => (PayLineKind?)null,
            };
            if (kind is null)
            {
                continue;
            }

            var assignedPeriods = periodsOf[record.Date].ToList();
            if (!record.IsComplete)
            {
                // Ordinary attendance of a fixed-salary employee pays nothing, so it is only a problem on a day that has a work assignment.
                if (kind != PayLineKind.FieldShift || assignedPeriods.Count > 0)
                {
                    incomplete.Add(record.Date);
                }

                continue;
            }

            var settings = AttendanceSettingsVersion.EffectiveOn(data.Settings, record.Date);
            var day = WorkHoursCalculator.Calculate(record.CheckInAt, record.CheckOutAt, assignedPeriods, settings);
            foreach (var job in day.Jobs.Where(j => j.Minutes > 0))
            {
                var jobType = data.JobTypes.GetValueOrDefault(job.JobTypeId);
                var jobName = jobType?.Name ?? string.Empty;
                if (kind == PayLineKind.Hourly)
                {
                    var rate = jobType is null ? null : PayRate.EffectiveOn(jobType.Rates, record.Date);
                    if (rate is null)
                    {
                        unrated.Add(new UnratedDay(record.Date, jobName, kind.Value));
                        continue;
                    }

                    days.Add(new SlipDay(record.Date, job.JobTypeId, jobName, kind.Value, job.Minutes, 0, rate.HourlyRate, Math.Round(job.Minutes * rate.HourlyRate / 60m, 0, MidpointRounding.AwayFromZero)));
                    continue;
                }

                var shifts = ShiftCounter.ShiftsOf(job.Minutes, settings);
                if (shifts <= 0)
                {
                    continue;
                }

                var shiftRate = jobType is null ? null : ShiftRate.EffectiveOn(jobType.ShiftRates, record.Date);
                if (shiftRate is null)
                {
                    unrated.Add(new UnratedDay(record.Date, jobName, kind.Value));
                    continue;
                }

                days.Add(new SlipDay(record.Date, job.JobTypeId, jobName, kind.Value, job.Minutes, shifts, shiftRate.Amount, Math.Round(shifts * shiftRate.Amount, 0, MidpointRounding.AwayFromZero)));
            }
        }

        // Rounded once per job type, kind and rate over the whole period, so a sum of per-day roundings does not drift from the sheet.
        var jobs = days
            .GroupBy(d => (d.JobTypeId, d.Kind, d.Rate))
            .Select(g =>
            {
                var minutes = g.Sum(d => d.Minutes);
                var shifts = g.Sum(d => d.Shifts);
                var amount = g.Key.Kind == PayLineKind.Hourly
                    ? Math.Round(minutes * g.Key.Rate / 60m, 0, MidpointRounding.AwayFromZero)
                    : Math.Round(shifts * g.Key.Rate, 0, MidpointRounding.AwayFromZero);
                return new SlipJobFigure(g.Key.JobTypeId, g.First().JobName, g.Key.Kind, g.Key.Rate, minutes, shifts, amount);
            })
            .OrderBy(j => j.JobName, StringComparer.CurrentCultureIgnoreCase).ThenBy(j => j.Kind).ThenBy(j => j.Rate)
            .ToList();
        var fixedSetup = EmployeeSalarySetup.EffectiveOn(employee.SalarySetups, to);
        return new SlipFigures(employee, jobs, incomplete, unrated)
        {
            Days = days,
            FixedPay = fixedSetup is { Type: SalaryType.FixedMonthly, MonthlyAmount: { } amount } ? amount : 0,
            SalaryType = SalaryPlan.TypeOn(employee, to),
            MissingSetup = SalaryPlan.IsMissingSetup(employee, from, to),
        };
    }
}
