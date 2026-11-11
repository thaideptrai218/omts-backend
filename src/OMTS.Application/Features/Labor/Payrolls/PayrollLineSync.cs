using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Labor.Payrolls;

/// <summary>
/// Brings the payslips of a Provisional sheet in line with the records: each employee with hours has one payslip whose job rows, hours,
/// pay and rate note follow the figures, and whose deduction previews the advances that finalizing would take off. Used by the creation,
/// the reload and the finalization, so all three give the same figures (BR-LABOR-10).
/// </summary>
public sealed class PayrollLineSync(IPayrollSheetRepository sheets, IWagePaymentRepository payments, IDocumentNumberAllocator numbers)
{
    /// <summary>The employees of the sheet that have a payslip, after the sync.</summary>
    /// <param name="dropEmpty">A payslip whose employee now has no hours is removed (reload All); otherwise it stays with zero hours.</param>
    public async Task SyncAsync(Payroll sheet, IReadOnlyList<SlipFigures> figures, bool dropEmpty, CancellationToken cancellationToken)
    {
        var existing = sheet.Lines.ToDictionary(l => l.EmployeeId);
        var wanted = figures.Where(f => f.Minutes > 0 || (!dropEmpty && existing.ContainsKey(f.Employee.Id))).ToList();
        foreach (var line in existing.Values.Where(l => wanted.All(f => f.Employee.Id != l.EmployeeId)).ToList())
        {
            foreach (var job in line.Jobs.ToList())
            {
                sheets.Remove(job);
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
            var row = new PayrollLineJob { PayrollLineId = line.Id, JobTypeId = job.JobTypeId, JobName = job.JobName, HourlyRate = job.HourlyRate, Hours = job.Hours, Amount = job.Amount };
            line.Jobs.Add(row);
            sheets.Add(row);
        }

        line.Hours = figure.Hours;
        line.MainPay = figure.MainPay;
        line.HourlyRateNote = figure.RateNote;
        line.Deduction = Math.Min(line.Income, advanceBalance);
    }
}
