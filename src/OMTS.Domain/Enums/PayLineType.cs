namespace OMTS.Domain.Enums;

/// <summary>Kind of payslip line (E43, LAB-07). Deduction and AdvanceDeduction lower the net pay; the others are earnings.</summary>
public enum PayLineType
{
    DailyWage,
    MonthlySalary,
    Piecework,
    Allowance,
    Deduction,
    Addition,
    AdvanceDeduction,
}
