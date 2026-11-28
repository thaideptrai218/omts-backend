using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Payrolls;

/// <summary>
/// Brings the payslips of a Provisional sheet in line with the records: each employee with pay (or a missing salary setup to show) has one payslip whose job rows, hours,
/// shifts, pay and rate note follow the figures, whose bonus and manual deduction follow the adjustments the Owner entered (kept through every recalculation), and whose
/// advance deduction previews the advances that finalizing would take off. Used by the creation, the reload and the finalization, so all three give the same figures (BR-LABOR-10).
/// </summary>
public sealed class PayrollLineSync(IPayrollSheetRepository sheets, IWagePaymentRepository payments, IDocumentNumberAllocator numbers)
{
    /// <summary>The employees of the sheet that have a payslip, after the sync.</summary>
    /// <param name="dropEmpty">A payslip whose employee now has nothing to pay and no adjustments is removed (reload All); otherwise it stays with zero pay.</param>
    public async Task SyncAsync(Payroll sheet, IReadOnlyList<SlipFigures> figures, bool dropEmpty, CancellationToken cancellationToken)
    {
        var existing = sheet.Lines.ToDictionary(l => l.EmployeeId);
        var wanted = figures.Where(f => f.OnSheet || (existing.TryGetValue(f.Employee.Id, out var kept) && (!dropEmpty || kept.Adjustments.Count > 0))).ToList();
        foreach (var line in existing.Values.Where(l => wanted.All(f => f.Employee.Id != l.EmployeeId)).ToList())
        {
            foreach (var job in line.Jobs.ToList())
            {
                sheets.Remove(job);
            }

            foreach (var adjustment in line.Adjustments.ToList())
            {
                sheets.Remove(adjustment);
            }

            sheet.Lines.Remove(line);
            sheets.Remove(line);
        }

        var balances = (await payments.ListOpenAdvancesAsync(wanted.Select(f => f.Employee.Id).ToList(), cancellationToken))
            .GroupBy(a => a.Advance.EmployeeId).ToDictionary(g => g.Key, g => g.Sum(a => a.Remaining));
        foreach (var figure in wanted.OrderBy(f => f.Employee.FullName, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!existing.TryGetValue(figure.Employee.Id, out var line))
            {
                line = new PayrollLine
                {
                    PayrollId = sheet.Id,
                    EmployeeId = figure.Employee.Id,
                    Code = PayrollRules.CodeOf(DocumentSequenceKeys.PayrollLine, await numbers.NextAsync(DocumentSequenceKeys.PayrollLine, cancellationToken)),
                };
                sheet.Lines.Add(line);
                sheets.Add(line);
            }

            Apply(line, figure, balances.GetValueOrDefault(figure.Employee.Id));
        }
    }

    private void Apply(PayrollLine line, SlipFigures figure, decimal advanceBalance)
    {
        foreach (var old in line.Jobs.ToList())
        {
            line.Jobs.Remove(old);
            sheets.Remove(old);
        }

        foreach (var job in figure.Jobs)
        {
            var row = new PayrollLineJob
            {
                PayrollLineId = line.Id,
                JobTypeId = job.JobTypeId,
                JobName = job.JobName,
                Kind = job.Kind,
                HourlyRate = job.HourlyRate,
                ShiftRate = job.ShiftRate,
                Hours = job.Hours,
                Shifts = job.Shifts,
                Amount = job.Amount,
            };
            line.Jobs.Add(row);
            sheets.Add(row);
        }

        line.SalaryType = figure.SalaryType;
        line.Hours = figure.Hours;
        line.Shifts = figure.Shifts;
        line.MainPay = figure.MainPay;
        line.ShiftPay = figure.ShiftPay;
        line.HourlyRateNote = figure.RateNote;
        SumAdjustments(line);
        line.Deduction = AdvanceDeductionOf(line.Income, advanceBalance);
    }

    /// <summary>The bonus and the manual deduction of a payslip are the sums of its + and − adjustments.</summary>
    public static void SumAdjustments(PayrollLine line)
    {
        line.Bonus = line.Adjustments.Where(a => a.Kind == PayrollAdjustmentKind.Bonus).Sum(a => a.Amount);
        line.ManualDeduction = line.Adjustments.Where(a => a.Kind == PayrollAdjustmentKind.Deduction).Sum(a => a.Amount);
    }

    /// <summary>Advances come off what is earned and never more than it: the rest stays an advance for the next payroll (BR-LABOR-11).</summary>
    public static decimal AdvanceDeductionOf(decimal income, decimal advanceBalance) => Math.Min(Math.Max(income, 0), advanceBalance);
}
