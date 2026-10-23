using System.Globalization;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.LaborCost.Calculation;

/// <summary>
/// LAB-07 calculation of one payee for one month (pure, unit-tested). Paid days = Present + PaidLeave records of
/// the month in all zones; UnpaidLeave is not paid and hours never change pay.
/// <list type="bullet">
/// <item>MonthlyFixed: the Amount of the month's version, in full (never prorated).</item>
/// <item>PerWorkingDay: Amount × paid days ÷ StandardWorkingDays, rounded up to 1,000 (one line per version used).</item>
/// <item>DailyWage: one line per paid day at the rate valid on that day.</item>
/// <item>Piecework: one line per confirmed assignment, CompletedQuantity × PieceRate rounded up to 1,000.</item>
/// <item>Allowances PerPaidDay: amount × paid days of the version valid on each day; PerMonth allowances and
/// deductions: the month's version, only when the payee has a salary line in the month.</item>
/// </list>
/// The month's version is the one valid on the last day of the month, else the latest one valid during the month.
/// A paid day without a valid version is reported as missing (missingRate) and gives no line.
/// </summary>
public static class PayrollCalculator
{
    public static PayeeCalculation Calculate(PayrollPeriod period, PayeeMonth input)
    {
        var rates = input.Rates
            .Where(r => r.ValidFrom <= period.End && (r.ValidTo is null || r.ValidTo >= period.Start))
            .OrderBy(r => r.ValidFrom)
            .ToList();
        var monthVersion = rates.LastOrDefault(r => r.IsValidOn(period.End)) ?? rates.LastOrDefault();
        var lines = new List<CalculatedLine>();
        var missing = new List<DateOnly>();
        var workingDays = new Dictionary<PayRuleVersion, int>();
        var perDayAllowances = new Dictionary<(PayRuleVersion Version, int Index), int>();

        foreach (var day in input.Attendance.Where(a => a.IsPaid && period.Contains(a.Date)).OrderBy(a => a.Date))
        {
            var version = rates.LastOrDefault(r => r.IsValidOn(day.Date));
            if (version is null)
            {
                missing.Add(day.Date);
                continue;
            }

            if (version.SalaryType == SalaryType.DailyWage)
            {
                lines.Add(new CalculatedLine(
                    PayLineType.DailyWage, $"Daily wage {Date(day.Date)}", day.Date, 1, version.Amount,
                    VndAmounts.RoundUpToThousand(version.Amount), version.Id, day.AttendanceId));
            }
            else if (version.SalaryType == SalaryType.PerWorkingDay)
            {
                workingDays[version] = workingDays.GetValueOrDefault(version) + 1;
            }

            for (var i = 0; i < version.Components.Count; i++)
            {
                if (version.Components[i] is { Kind: PayComponentKind.Allowance, Basis: PayComponentBasis.PerPaidDay })
                {
                    perDayAllowances[(version, i)] = perDayAllowances.GetValueOrDefault((version, i)) + 1;
                }
            }
        }

        foreach (var (version, days) in workingDays.OrderBy(w => w.Key.ValidFrom))
        {
            var standard = version.StandardWorkingDays is > 0 and var s ? s : 26;
            lines.Add(new CalculatedLine(
                PayLineType.MonthlySalary, $"Salary for {days}/{standard} working days", period.End, days, version.Amount,
                VndAmounts.RoundUpToThousand(version.Amount * days / standard), version.Id));
        }

        if (input.PaysMonthlySalary && monthVersion is { SalaryType: SalaryType.MonthlyFixed })
        {
            lines.Add(new CalculatedLine(
                PayLineType.MonthlySalary, "Monthly salary", period.End, 1, monthVersion.Amount,
                VndAmounts.RoundUpToThousand(monthVersion.Amount), monthVersion.Id));
        }

        var hasSalary = lines.Count > 0;
        foreach (var ((version, index), days) in perDayAllowances.OrderBy(a => a.Key.Version.ValidFrom).ThenBy(a => a.Key.Index))
        {
            var component = version.Components[index];
            lines.Add(new CalculatedLine(
                PayLineType.Allowance, $"{component.Name} ({days} paid days)", period.End, days, component.Amount,
                VndAmounts.RoundUpToThousand(component.Amount * days), version.Id));
        }

        if (hasSalary && monthVersion is not null)
        {
            foreach (var component in monthVersion.Components.Where(c => c.Basis == PayComponentBasis.PerMonth || c.Kind == PayComponentKind.Deduction))
            {
                var type = component.Kind == PayComponentKind.Allowance ? PayLineType.Allowance : PayLineType.Deduction;
                lines.Add(new CalculatedLine(
                    type, component.Name, period.End, 1, component.Amount, VndAmounts.RoundUpToThousand(component.Amount), monthVersion.Id));
            }
        }

        foreach (var work in input.Piecework.Where(p => period.Contains(p.CompletionDate) && p.CompletedQuantity > 0).OrderBy(p => p.CompletionDate))
        {
            lines.Add(new CalculatedLine(
                PayLineType.Piecework,
                $"{work.Task}: {Number(work.CompletedQuantity)} {work.PieceUnit} × {VndAmounts.Format(work.PieceRate)}",
                work.CompletionDate, work.CompletedQuantity, work.PieceRate,
                VndAmounts.RoundUpToThousand(work.CompletedQuantity * work.PieceRate), AssignmentId: work.AssignmentId));
        }

        return new PayeeCalculation(input.Payee, lines, missing);
    }

    private static string Date(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
