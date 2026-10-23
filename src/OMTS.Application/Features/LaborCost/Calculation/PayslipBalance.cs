using OMTS.Domain.Enums;

namespace OMTS.Application.Features.LaborCost.Calculation;

/// <summary>
/// Net pay of a payslip (LAB-07): earnings − deductions − advance deduction, never negative. Outstanding advances
/// are deducted only up to what is left after the other deductions (the advance cap); the rest stays outstanding.
/// </summary>
public static class PayslipBalance
{
    public static bool IsEarning(PayLineType type) => type is not (PayLineType.Deduction or PayLineType.AdvanceDeduction);

    /// <param name="lines">Every line except the advance deduction (system and manual).</param>
    /// <param name="outstandingAdvances">Advances of the payee not yet deducted by a finalized payroll.</param>
    public static PayslipTotals Settle(IEnumerable<(PayLineType Type, decimal Amount)> lines, decimal outstandingAdvances)
    {
        var list = lines.Where(l => l.Type != PayLineType.AdvanceDeduction).ToList();
        var earnings = list.Where(l => IsEarning(l.Type)).Sum(l => l.Amount);
        var deductions = list.Where(l => l.Type == PayLineType.Deduction).Sum(l => l.Amount);
        var available = Math.Max(0, earnings - deductions);
        var advance = Math.Min(Math.Max(0, outstandingAdvances), available);
        return new PayslipTotals(earnings, deductions, advance, Math.Max(0, earnings - deductions - advance));
    }

    /// <summary>Totals of stored lines (the advance deduction line is already in them).</summary>
    public static PayslipTotals Of(IEnumerable<(PayLineType Type, decimal Amount)> lines)
    {
        var list = lines.ToList();
        var earnings = list.Where(l => IsEarning(l.Type)).Sum(l => l.Amount);
        var deductions = list.Where(l => l.Type == PayLineType.Deduction).Sum(l => l.Amount);
        var advance = list.Where(l => l.Type == PayLineType.AdvanceDeduction).Sum(l => l.Amount);
        return new PayslipTotals(earnings, deductions, advance, Math.Max(0, earnings - deductions - advance));
    }
}
