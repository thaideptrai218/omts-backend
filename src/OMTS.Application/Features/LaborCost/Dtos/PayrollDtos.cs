using OMTS.Domain.Enums;

namespace OMTS.Application.Features.LaborCost.Dtos;

/// <param name="Period">"YYYY-MM".</param>
public sealed record CreatePayrollRequest(string? Period);

/// <summary>Manual line of a Draft payslip: Addition or Deduction, amount a multiple of 1,000, reason required.</summary>
public sealed record AddManualLineRequest(PayLineType? Type, decimal? Amount, string? Reason);

/// <summary>Send back or reopen: reason required (at most 500 characters).</summary>
public sealed record PayrollReasonRequest(string? Reason);

/// <param name="Kind">Employee, Worker or Team.</param>
public sealed record PayeeRef(string Kind, Guid Id, string Name);

public sealed record PayLineView(
    Guid Id, PayLineType Type, string? Description, DateOnly PayDate, decimal? Quantity, decimal? UnitRate, decimal Amount, bool IsManual, string? Reason);

/// <param name="Reversed">A negative payment cancels this one.</param>
public sealed record PaymentView(
    Guid Id,
    string Recipient,
    decimal AmountPaid,
    DateOnly PaymentDate,
    PaymentMethod Method,
    string? ReferenceCode,
    string? Notes,
    Guid? OriginalPaymentId,
    string? AdjustmentReason,
    bool Reversed,
    IReadOnlyList<string> DocumentUrls);

/// <param name="Gross">Earnings: wages, salary, piecework, allowances and additions.</param>
/// <param name="Deductions">Deduction lines (without the advance deduction).</param>
/// <param name="MissingRate">A paid day has no valid pay rate: the payroll cannot be finalized (MSG50).</param>
public sealed record PayslipView(
    Guid Id,
    string Period,
    PayeeRef Payee,
    int StatusId,
    string Status,
    decimal Gross,
    decimal Deductions,
    decimal AdvanceDeduction,
    decimal Net,
    decimal Paid,
    decimal Remaining,
    bool MissingRate,
    IReadOnlyList<DateOnly> MissingRateDates,
    IReadOnlyList<PayLineView> Lines,
    IReadOnlyList<PaymentView> Payments,
    string RowVersion);

public sealed record PayrollTotals(int PayslipCount, decimal Gross, decimal Deductions, decimal AdvanceDeduction, decimal Net, decimal Paid);

/// <param name="State">Draft, Sent back, Finalized or Fully paid (derived; the payroll has no status of its own).</param>
public sealed record PayrollListItem(
    string Period, DateOnly PeriodStart, DateOnly PeriodEnd, string State, PayrollTotals Totals, bool HasMissingRates, DateTimeOffset? FinalizedAt);

public sealed record PayrollDetail(
    string Period,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string State,
    string? ReturnReason,
    DateTimeOffset? ReturnedAt,
    DateTimeOffset? FinalizedAt,
    PayrollTotals Totals,
    IReadOnlyList<PayslipView> Payslips,
    string RowVersion);
