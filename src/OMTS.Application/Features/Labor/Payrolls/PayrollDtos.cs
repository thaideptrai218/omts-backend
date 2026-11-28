using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Payrolls;

/// <summary>The status of a payroll sheet as the web contract names it; Provisional is the catalog's Draft (and the SRS states no endpoint reaches).</summary>
public enum PayrollSheetStatus
{
    Creating,
    Provisional,
    Finalized,
    Cancelled,
}

/// <summary>What a reload recalculates: every line (the employees included), or only the hours and pay of the lines the sheet has.</summary>
public enum PayrollReloadMode
{
    All,
    WorkingDaysOnly,
}

/// <param name="PeriodType">Monthly (a whole calendar month) or Custom.</param>
/// <param name="From">First day of the period.</param>
/// <param name="To">Last day of the period.</param>
/// <param name="Scope">All employees with pay in the period (attendance paid by the hour or the shift, a fixed salary, or a Staff employee with no salary setup), only the Staff or only the SeasonalWorker employees (their type on the last day of the period), or the Selected ones named in <paramref name="EmployeeIds"/> (1 to 500).</param>
public sealed record CreatePayrollRequest(
    PayrollPeriodType? PeriodType, DateOnly? From, DateOnly? To, PayrollScope? Scope, IReadOnlyList<Guid>? EmployeeIds);

public sealed record ReloadPayrollRequest(PayrollReloadMode? Mode);

/// <param name="ExpenseMonth">yyyy-MM: the month the wages count in as a cost.</param>
/// <param name="ExpectedTotal">The total of the sheet as the Owner last saw it (the detail's <c>total</c>); when the recalculated total differs, 409 and nothing is finalized (L11). Optional.</param>
/// <param name="ExpectedSlips">The number of payslips as last seen; checked with <paramref name="ExpectedTotal"/>. Optional.</param>
public sealed record FinalizePayrollRequest(string? ExpenseMonth, decimal? ExpectedTotal = null, int? ExpectedSlips = null);

/// <summary>
/// One reason a sheet cannot be finalized, as data. <c>Kind</c> is PeriodNotEnded, IncompleteAttendance, NoRate (hours without an hourly rate), NoShiftRate (shifts without a shift rate), NoSalarySetup
/// (a Staff employee with no salary setup: "Chưa thiết lập lương"), NegativePay (deductions above the income of a payslip) or NoHours; <c>Dates</c> are the days concerned.
/// </summary>
public sealed record NotReadyItem(string Kind, Guid? EmployeeId, string? EmployeeName, IReadOnlyList<DateOnly> Dates, string? JobName);

/// <param name="Method">Cash or BankTransfer.</param>
/// <param name="PaidById">The Active Owner or staff account that handed the money over ("Nhân viên tạo"); the signed-in account when null. 400 "Paid by" for any other.</param>
/// <param name="Lines">The payslips of the sheet to pay, each from 1 to what is still owed.</param>
/// <param name="CashAccountId">With BankTransfer, the active Bank account (Sổ quỹ) the money is paid from; optional. 400 "Cash account" with Cash or an account that is not an active Bank one.</param>
public sealed record PayPayrollRequest(DateTimeOffset? PaidAt, PaymentMethod? Method, string? Note, IReadOnlyList<PayPayrollLine>? Lines, Guid? CashAccountId = null, Guid? PaidById = null);

public sealed record PayPayrollLine(Guid? SlipId, decimal? Amount);

public sealed record StandalonePayrollPaymentRequest(Guid? EmployeeId, decimal? Amount, DateTimeOffset? PaidAt, PaymentMethod? Method, string? Note, Guid? CashAccountId = null, Guid? PaidById = null);

/// <param name="Status">Repeated: <c>status=Creating&amp;status=Provisional</c>; none means every status.</param>
/// <param name="SortBy">code (default), name, from, total, paid or status.</param>
/// <param name="SortDir">asc or desc (default).</param>
public sealed record PayrollListQuery(
    string? Search = null,
    PayrollPeriodType? PeriodType = null,
    IReadOnlyList<PayrollSheetStatus>? Status = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? SortBy = null,
    string? SortDir = null,
    int Page = 1,
    int PageSize = 15);

/// <summary>The list query without paging, for the export.</summary>
public sealed record PayrollExportQuery(
    string? Search = null,
    PayrollPeriodType? PeriodType = null,
    IReadOnlyList<PayrollSheetStatus>? Status = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? SortBy = null,
    string? SortDir = null);

public sealed record PayrollPaymentListQuery(string? Search = null, Guid? EmployeeId = null, DateOnly? From = null, DateOnly? To = null, int Page = 1, int PageSize = 20, Guid? PayrollId = null,
    string? Method = null, string? Kind = null, string? Status = null);

public sealed record EmployeePayslipListQuery(PayrollSheetStatus? Status = null, int Page = 1, int PageSize = 20);

public sealed record PayrollSheetRow(
    Guid Id, string Code, string Name, PayrollPeriodType PeriodType, DateOnly From, DateOnly To,
    decimal Total, decimal Paid, decimal Outstanding, PayrollSheetStatus Status, DateTimeOffset CreatedAt);

public sealed record PayrollTotals(decimal Total, decimal Paid, decimal Outstanding);

public sealed record PayrollSheetPage(IReadOnlyList<PayrollSheetRow> Items, int Total, int Page, int PageSize, PayrollTotals Summary);

/// <summary>One + or − line of a payslip.</summary>
public sealed record SlipAdjustmentView(PayrollAdjustmentKind Kind, decimal Amount, string Reason);

/// <summary>One line the Owner sends: <c>Kind</c> Bonus (+) or Deduction (−), a whole amount above 0 and the reason (1 to 200 characters).</summary>
public sealed record SlipAdjustmentInput(PayrollAdjustmentKind? Kind, decimal? Amount, string? Reason);

/// <summary>The whole list of adjustments of a payslip (an empty list removes them all).</summary>
public sealed record SaveSlipAdjustmentsRequest(IReadOnlyList<SlipAdjustmentInput>? Items);

/// <summary>
/// A payslip as the sheet lists it. Net = MainPay + ShiftPay + Bonus - ManualDeduction - AdvanceDeduction; Income is the part before the advances.
/// </summary>
/// <param name="SalaryType">The salary type in force on the last day of the period; null for a Staff employee with no setup ("Chưa thiết lập lương").</param>
/// <param name="Hours">Hours worked that were paid by the hour or the shift.</param>
/// <param name="Shifts">Shifts counted (per-shift pay and field shifts).</param>
/// <param name="MainPay">The fixed monthly salary, the hourly pay and the per-shift pay.</param>
/// <param name="ShiftPay">The shifts of field work paid to a fixed-salary employee on top of the salary.</param>
/// <param name="Bonus">The sum of the + adjustments.</param>
/// <param name="ManualDeduction">The sum of the − adjustments.</param>
/// <param name="AdvanceDeduction">The advances taken off (a preview until the sheet is finalized).</param>
/// <param name="AllowsAdjustments">True for an internal (Staff) employee: only then may the Owner enter adjustments.</param>
public sealed record PayrollSlipRow(
    Guid Id, string Code, Guid EmployeeId, string EmployeeCode, string EmployeeName, SalaryType? SalaryType, decimal Hours, decimal Shifts, string? HourlyRateNote,
    decimal MainPay, decimal ShiftPay, decimal Bonus, decimal ManualDeduction, decimal AdvanceDeduction, decimal Income, decimal Net, decimal Paid, decimal Outstanding,
    IReadOnlyList<SlipAdjustmentView> Adjustments, bool AllowsAdjustments);

public sealed record PayrollSheetDetail(
    Guid Id, string Code, string Name, PayrollPeriodType PeriodType, DateOnly From, DateOnly To, PayrollScope Scope, PayrollSheetStatus Status,
    string? ExpenseMonth, string? Note, string? CreatedBy, DateTimeOffset CreatedAt, decimal Total, decimal Paid, decimal Outstanding,
    IReadOnlyList<PayrollSlipRow> Slips, string? CreatedByName, int EmployeeCount, DateTimeOffset? CalculatedAt = null,
    string? FinalizedBy = null, DateTimeOffset? FinalizedAt = null);

public sealed record PayrollVoucherRef(Guid Id, string Code, Guid SlipId, string EmployeeName, decimal Amount);

public sealed record PayrollPaymentResult(IReadOnlyList<PayrollVoucherRef> Vouchers);

public sealed record StandalonePaymentResult(Guid Id);

public sealed record PayrollPaymentRow(
    Guid Id, string VoucherCode, DateTimeOffset PaidAt, Guid EmployeeId, string EmployeeName, Guid? PayrollId, string? PayrollCode,
    PaymentMethod Method, decimal Amount, WagePaymentKind Kind, string Status, string? PayrollName = null);

/// <summary><c>Amount</c> is the sum of the Recorded (not cancelled) payments of the whole filtered set, not of the page.</summary>
/// <summary>
/// What a payment voucher paid. A Wage payment pays one payslip: <c>SlipNet</c> is the net pay, <c>PaidBefore</c> what was paid on it before this voucher, <c>Amount</c> this voucher's part and <c>SlipStatus</c>
/// (Unpaid, PartiallyPaid or Paid) the state of the payslip once this voucher counts (without it when the voucher is cancelled).
/// </summary>
public sealed record PayrollPaymentAllocation(
    Guid SlipId, string SlipCode, Guid PayrollId, string PayrollCode, string PeriodName, DateOnly From, DateOnly To, DateTimeOffset? FinalizedAt,
    decimal SlipNet, decimal PaidBefore, decimal Amount, string SlipStatus);

/// <summary>
/// A wage or advance payment voucher. <c>Id</c> is the id of the cash voucher; <c>RowVersion</c> goes back with an edit or a cancel. <c>CreatedBy</c> is the account that recorded it, <c>PaidBy</c> the Owner or staff
/// account that handed the money over. A Wage voucher has one allocation; an Advance one has none and shows <c>AdvanceDeducted</c>, what later payslips have taken off. <c>Status</c> is Recorded or Cancelled.
/// </summary>
public sealed record PayrollPaymentDetail(
    Guid Id, string Code, WagePaymentKind Kind, Guid EmployeeId, string? EmployeeCode, string EmployeeName, DateTimeOffset PaidAt, PaymentMethod Method,
    Guid? CashAccountId, string? CashAccountName, string? Note, Guid? CreatedById, string? CreatedBy, DateTimeOffset CreatedAt, Guid? PaidById, string? PaidBy,
    string Status, DateTimeOffset? CancelledAt, string? CancelReason, decimal Total, string RowVersion,
    IReadOnlyList<PayrollPaymentAllocation> Allocations, decimal AdvanceAmount, decimal AdvanceDeducted);

/// <summary>The fields a payment voucher may change; its amount, employee and payslip never change. <c>PaidAt</c> null keeps the time.</summary>
public sealed record UpdatePayrollPaymentRequest(
    DateTimeOffset? PaidAt, PaymentMethod? Method, Guid? CashAccountId, string? Note, Guid? PaidById, string? RowVersion);

public sealed record CancelPayrollPaymentRequest(string? Reason, string? RowVersion);

public sealed record PayrollPayer(Guid Id, string Name);

public sealed record PayrollPaymentSummary(decimal Amount);

public sealed record PayrollPaymentPage(IReadOnlyList<PayrollPaymentRow> Items, int Total, int Page, int PageSize, PayrollPaymentSummary Summary);

public sealed record PayableEmployee(Guid EmployeeId, string Code, string Name, decimal Outstanding);

public sealed record PayableEmployees(IReadOnlyList<PayableEmployee> Items);

public sealed record EmployeeSlipRow(
    Guid Id, string Code, Guid PayrollId, string PayrollCode, string PayrollName, DateOnly From, DateOnly To,
    decimal Income, decimal Net, decimal Paid, decimal Outstanding, PayrollSheetStatus Status, DateTimeOffset? FinalizedAt = null);

public sealed record EmployeeSlipSummary(decimal Outstanding, decimal AdvanceBalance);

public sealed record EmployeeSlipPage(IReadOnlyList<EmployeeSlipRow> Items, int Total, int Page, int PageSize, EmployeeSlipSummary Summary);

/// <summary>One job type at one rate. <c>Kind</c> Hourly (hours x HourlyRate), Shift (Shifts x ShiftRate, the main pay of a per-shift employee) or FieldShift (Shifts x ShiftRate, the shift pay of a fixed-salary employee).</summary>
public sealed record PayrollSlipJobLine(
    string JobName, decimal Hours, decimal HourlyRate, decimal Amount, PayLineKind Kind = PayLineKind.Hourly, decimal Shifts = 0, decimal ShiftRate = 0);

public sealed record PayrollSlipPayment(Guid Id, string VoucherCode, DateTimeOffset PaidAt, PaymentMethod Method, decimal Amount, WagePaymentKind Kind, string Status);

/// <summary>
/// One job type on one day: the rafts worked, the check-in and check-out, the hours, the shifts counted, the rate in force (hourly for <c>Kind</c> Hourly, the shift rate otherwise) and the amount
/// (hours x rate or shifts x rate, to the VND) so the payslip can be explained day by day.
/// </summary>
public sealed record PayrollSlipAttendanceDay(
    DateOnly Date, string JobName, decimal Hours, string? RaftCodes = null, DateTimeOffset? CheckInAt = null, DateTimeOffset? CheckOutAt = null, decimal HourlyRate = 0, decimal Amount = 0,
    PayLineKind Kind = PayLineKind.Hourly, decimal Shifts = 0, decimal ShiftRate = 0);

/// <param name="FixedPay">The fixed monthly salary inside <c>MainPay</c> (0 on a Custom sheet and for the other types); the rest of <c>MainPay</c> is the sum of the Hourly and Shift lines.</param>
public sealed record PayrollSlipDetail(
    Guid Id, string Code, Guid PayrollId, string PayrollCode, Guid EmployeeId, string EmployeeName, DateOnly From, DateOnly To, decimal Hours,
    decimal MainPay, decimal Bonus, decimal AdvanceDeduction, decimal Income, decimal Net, decimal Paid, decimal Outstanding, PayrollSheetStatus Status,
    IReadOnlyList<PayrollSlipJobLine> Lines, IReadOnlyList<PayrollSlipPayment> Payments, IReadOnlyList<PayrollSlipAttendanceDay> Attendance, string? EmployeeCode = null,
    SalaryType? SalaryType = null, decimal Shifts = 0, decimal ShiftPay = 0, decimal ManualDeduction = 0, decimal FixedPay = 0,
    IReadOnlyList<SlipAdjustmentView>? Adjustments = null, bool AllowsAdjustments = false);
