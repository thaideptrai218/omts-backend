using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.LaborCost.Calculation;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.LaborCost;

/// <summary>
/// Writes the calculation into the Draft payslips of a payroll (create and refresh, LAB-07): system lines are replaced,
/// manual lines kept, the advance deduction re-capped and the net pay recomputed. A payslip left with nothing (no line,
/// no missing rate, no manual line) is removed, since it is derived data of a draft.
/// </summary>
public sealed class PayrollSync(IPayrollRepository payrolls)
{
    /// <returns>The payslips the payroll keeps (removed ones are only deleted when the unit of work saves).</returns>
    public IReadOnlyList<LaborSettlement> Apply(Payroll payroll, IReadOnlyList<PayeeComputation> computations, Guid preparedBy)
    {
        var existing = payroll.Settlements.ToDictionary(PayslipViews.KeyOf);
        var kept = new List<LaborSettlement>();
        foreach (var computation in computations)
        {
            existing.TryGetValue(computation.Payee, out var settlement);
            var manual = settlement?.PayLines.Where(l => l.IsManual).ToList() ?? [];
            if (!computation.Calculation.NeedsPayslip && !computation.AlwaysPayslip && manual.Count == 0)
            {
                if (settlement is not null)
                {
                    foreach (var line in settlement.PayLines.ToList())
                    {
                        payrolls.RemoveLine(line);
                    }

                    payrolls.RemoveSettlement(settlement);
                }

                continue;
            }

            if (settlement is null)
            {
                settlement = New(payroll, computation.Payee, preparedBy);
                payrolls.AddSettlement(settlement);
            }

            foreach (var line in settlement.PayLines.Where(l => !l.IsManual).ToList())
            {
                payrolls.RemoveLine(line);
            }

            var lines = computation.Calculation.Lines.Select(l => ToPayLine(settlement, l)).ToList();
            foreach (var line in lines)
            {
                payrolls.AddLine(line);
            }

            SetAdvanceAndTotal(settlement, [.. manual, .. lines], computation.OutstandingAdvances, null);
            settlement.MissingRateDates = [.. computation.Calculation.MissingRateDates];
            kept.Add(settlement);
        }

        return kept;
    }

    /// <summary>
    /// Re-caps the advance deduction line (removing <paramref name="currentAdvanceLine"/>) and sets the net pay from
    /// <paramref name="otherLines"/> (every line except the advance deduction).
    /// </summary>
    public void SetAdvanceAndTotal(LaborSettlement settlement, IReadOnlyList<PayLine> otherLines, decimal outstanding, PayLine? currentAdvanceLine)
    {
        if (currentAdvanceLine is not null)
        {
            payrolls.RemoveLine(currentAdvanceLine);
        }

        var totals = PayslipBalance.Settle(otherLines.Select(l => (l.LineType, l.Amount)), outstanding);
        if (totals.AdvanceDeduction > 0)
        {
            payrolls.AddLine(ToPayLine(settlement, new CalculatedLine(
                PayLineType.AdvanceDeduction, "Salary advances", settlement.PeriodEnd, null, null, totals.AdvanceDeduction)));
        }

        settlement.TotalObligation = totals.Net;
    }

    public static PayLine ToPayLine(LaborSettlement settlement, CalculatedLine line) => new()
    {
        SettlementId = settlement.Id,
        EmployeeId = settlement.EmployeeId,
        WorkerId = settlement.WorkerId,
        LineType = line.Type,
        Description = line.Description,
        PayDate = line.PayDate,
        Quantity = line.Quantity,
        UnitRate = line.UnitRate,
        Amount = line.Amount,
        PayRuleVersionId = line.PayRuleVersionId,
        AttendanceId = line.AttendanceId,
        AssignmentId = line.AssignmentId,
    };

    private static LaborSettlement New(Payroll payroll, PayeeKey payee, Guid preparedBy) => new()
    {
        PayrollId = payroll.Id,
        EmployeeId = payee.Kind == PayeeKind.Employee ? payee.Id : null,
        WorkerId = payee.Kind == PayeeKind.Worker ? payee.Id : null,
        ContractorId = payee.Kind == PayeeKind.Team ? payee.Id : null,
        PeriodStart = payroll.PeriodStart,
        PeriodEnd = payroll.PeriodEnd,
        PreparedByAccountId = preparedBy,
    };
}
