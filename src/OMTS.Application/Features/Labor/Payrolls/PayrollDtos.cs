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
/// <param name="Scope">All employees with hours in the period, or the Selected ones named in <paramref name="EmployeeIds"/> (1 to 500).</param>
public sealed record CreatePayrollRequest(
    PayrollPeriodType? PeriodType, DateOnly? From, DateOnly? To, PayrollScope? Scope, IReadOnlyList<Guid>? EmployeeIds);

public sealed record ReloadPayrollRequest(PayrollReloadMode? Mode);

/// <param name="ExpenseMonth">yyyy-MM: the month the wages count in as a cost.</param>
/// <param name="ExpectedTotal">The total of the sheet as the Owner last saw it (the detail's <c>total</c>); when the recalculated total differs, 409 and nothing is finalized (L11). Optional.</param>
/// <param name="ExpectedSlips">The number of payslips as last seen; checked with <paramref name="ExpectedTotal"/>. Optional.</param>
public sealed record FinalizePayrollRequest(string? ExpenseMonth, decimal? ExpectedTotal = null, int? ExpectedSlips = null);

/// <summary>One reason a sheet cannot be finalized, as data. <c>Kind</c> is PeriodNotEnded, IncompleteAttendance, NoRate or NoHours; <c>Dates</c> are the days concerned.</summary>
public sealed record NotReadyItem(string Kind, Guid? EmployeeId, string? EmployeeName, IReadOnlyList<DateOnly> Dates, string? JobName);

/// <param name="Method">Cash or BankTransfer.</param>
/// <param name="Lines">The payslips of the sheet to pay, each from 1 to what is still owed.</param>
/// <param name="CashAccountId">With BankTransfer, the active Bank account (Sổ quỹ) the money is paid from; optional. 400 "Cash account" with Cash or an account that is not an active Bank one.</param>
public sealed record PayPayrollRequest(DateTimeOffset? PaidAt, PaymentMethod? Method, string? Note, IReadOnlyList<PayPayrollLine>? Lines, Guid? CashAccountId = null);

public sealed record PayPayrollLine(Guid? SlipId, decimal? Amount);

public sealed record StandalonePayrollPaymentRequest(Guid? EmployeeId, decimal? Amount, DateTimeOffset? PaidAt, PaymentMethod? Method, string? Note, Guid? CashAccountId = null);

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

public sealed record PayrollSlipRow(
    Guid Id, string Code, Guid EmployeeId, string EmployeeCode, string EmployeeName, decimal Hours, string? HourlyRateNote,
    decimal MainPay, decimal Bonus, decimal Deduction, decimal Income, decimal Net, decimal Paid, decimal Outstanding);

public sealed record PayrollSheetDetail(
    Guid Id, string Code, string Name, PayrollPeriodType PeriodType, DateOnly From, DateOnly To, PayrollScope Scope, PayrollSheetStatus Status,
    string? ExpenseMonth, string? Note, string? CreatedBy, DateTimeOffset CreatedAt, decimal Total, decimal Paid, decimal Outstanding,
    IReadOnlyList<PayrollSlipRow> Slips, string? CreatedByName, int EmployeeCount, DateTimeOffset? CalculatedAt = null);

public sealed record PayrollVoucherRef(Guid Id, string Code, Guid SlipId, string EmployeeName, decimal Amount);

public sealed record PayrollPaymentResult(IReadOnlyList<PayrollVoucherRef> Vouchers);

public sealed record StandalonePaymentResult(Guid Id);

public sealed record PayrollPaymentRow(
    Guid Id, string VoucherCode, DateTimeOffset PaidAt, Guid EmployeeId, string EmployeeName, Guid? PayrollId, string? PayrollCode,
    PaymentMethod Method, decimal Amount, WagePaymentKind Kind, string Status, string? PayrollName = null);

/// <summary><c>Amount</c> is the sum of the Recorded (not cancelled) payments of the whole filtered set, not of the page.</summary>
public sealed record PayrollPaymentSummary(decimal Amount);

public sealed record PayrollPaymentPage(IReadOnlyList<PayrollPaymentRow> Items, int Total, int Page, int PageSize, PayrollPaymentSummary Summary);

public sealed record PayableEmployee(Guid EmployeeId, string Code, string Name, decimal Outstanding);

public sealed record PayableEmployees(IReadOnlyList<PayableEmployee> Items);

public sealed record EmployeeSlipRow(
    Guid Id, string Code, Guid PayrollId, string PayrollCode, string PayrollName, DateOnly From, DateOnly To,
    decimal Income, decimal Net, decimal Paid, decimal Outstanding, PayrollSheetStatus Status);

public sealed record EmployeeSlipSummary(decimal Outstanding, decimal AdvanceBalance);

public sealed record EmployeeSlipPage(IReadOnlyList<EmployeeSlipRow> Items, int Total, int Page, int PageSize, EmployeeSlipSummary Summary);

public sealed record PayrollSlipJobLine(string JobName, decimal Hours, decimal HourlyRate, decimal Amount);

public sealed record PayrollSlipPayment(Guid Id, string VoucherCode, DateTimeOffset PaidAt, PaymentMethod Method, decimal Amount, WagePaymentKind Kind, string Status);

/// <summary>One job type on one day: the rafts worked, the check-in and check-out, the hours, the rate in force and the amount (hours x rate, to the VND) so the payslip can be explained day by day.</summary>
public sealed record PayrollSlipAttendanceDay(
    DateOnly Date, string JobName, decimal Hours, string? RaftCodes = null, DateTimeOffset? CheckInAt = null, DateTimeOffset? CheckOutAt = null, decimal HourlyRate = 0, decimal Amount = 0);

public sealed record PayrollSlipDetail(
    Guid Id, string Code, Guid PayrollId, string PayrollCode, Guid EmployeeId, string EmployeeName, DateOnly From, DateOnly To, decimal Hours,
    decimal MainPay, decimal Bonus, decimal Deduction, decimal Income, decimal Net, decimal Paid, decimal Outstanding, PayrollSheetStatus Status,
    IReadOnlyList<PayrollSlipJobLine> Lines, IReadOnlyList<PayrollSlipPayment> Payments, IReadOnlyList<PayrollSlipAttendanceDay> Attendance, string? EmployeeCode = null);
