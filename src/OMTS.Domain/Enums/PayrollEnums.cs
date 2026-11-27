namespace OMTS.Domain.Enums;

/// <summary>How the period of a payroll sheet was chosen: a whole calendar month, or any dates (PAYROLL_API_CONTRACT).</summary>
public enum PayrollPeriodType
{
    Monthly,
    Custom,
}

/// <summary>Whose pay a payroll sheet covers: every employee with hours in the period, or the employees chosen when it was made.</summary>
public enum PayrollScope
{
    All,
    Selected,
}

/// <summary>How the pay of one job row of a payslip was worked out: hours at the hourly rate, shifts of a per-shift employee, or the shifts of field work that a fixed-salary employee is paid on top.</summary>
public enum PayLineKind
{
    Hourly,
    Shift,
    FieldShift,
}

/// <summary>A manual line on the payslip of a Staff employee: a bonus adds to the pay, a deduction takes from it.</summary>
public enum PayrollAdjustmentKind
{
    Bonus,
    Deduction,
}

/// <summary>What a wage payment pays: a payslip of a Finalized payroll, or an advance that the next Finalized payroll of the employee deducts.</summary>
public enum WagePaymentKind
{
    Wage,
    Advance,
}
