using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.LaborCost.Calculation;

/// <summary>Who a payslip pays: an employee, a seasonal worker or a team.</summary>
public enum PayeeKind
{
    Employee,
    Worker,
    Team,
}

public readonly record struct PayeeKey(PayeeKind Kind, Guid Id);

/// <summary>One attendance record of the payee in the month (any zone).</summary>
public sealed record AttendanceDay(Guid AttendanceId, DateOnly Date, AttendanceResult Result)
{
    public bool IsPaid => Result is AttendanceResult.Present or AttendanceResult.PaidLeave;
}

/// <summary>Confirmed piecework of the payee whose completion date is in the month.</summary>
public sealed record ConfirmedPiecework(
    Guid AssignmentId, DateOnly CompletionDate, decimal CompletedQuantity, decimal PieceRate, string Task, string PieceUnit);

/// <summary>Everything the calculation of one payee for one month needs.</summary>
/// <param name="Rates">Pay rate versions of the person (any dates; those outside the month are ignored).</param>
/// <param name="PaysMonthlySalary">
/// False for workers, teams and employees whose employment ended before the month: they never get a monthly salary.
/// </param>
public sealed record PayeeMonth(
    PayeeKey Payee,
    IReadOnlyList<PayRuleVersion> Rates,
    IReadOnlyList<AttendanceDay> Attendance,
    IReadOnlyList<ConfirmedPiecework> Piecework,
    bool PaysMonthlySalary);

/// <summary>A system line before it is stored as a <see cref="PayLine"/>.</summary>
public sealed record CalculatedLine(
    PayLineType Type,
    string Description,
    DateOnly PayDate,
    decimal? Quantity,
    decimal? UnitRate,
    decimal Amount,
    Guid? PayRuleVersionId = null,
    Guid? AttendanceId = null,
    Guid? AssignmentId = null);

/// <summary>System lines (without the advance deduction) and the paid days that have no valid rate.</summary>
public sealed record PayeeCalculation(PayeeKey Payee, IReadOnlyList<CalculatedLine> Lines, IReadOnlyList<DateOnly> MissingRateDates)
{
    /// <summary>A payslip is made for a payee with a line or a missing-rate flag.</summary>
    public bool NeedsPayslip => Lines.Count > 0 || MissingRateDates.Count > 0;
}

/// <summary>Totals of a payslip; every amount is a multiple of 1,000 VND and <see cref="Net"/> is never negative.</summary>
public sealed record PayslipTotals(decimal Earnings, decimal Deductions, decimal AdvanceDeduction, decimal Net);
