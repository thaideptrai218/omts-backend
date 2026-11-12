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

/// <summary>What a wage payment pays: a payslip of a Finalized payroll, or an advance that the next Finalized payroll of the employee deducts.</summary>
public enum WagePaymentKind
{
    Wage,
    Advance,
}
