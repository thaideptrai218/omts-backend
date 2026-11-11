using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The payroll sheets the services change (LABOR-12, LABOR-13).</summary>
public interface IPayrollSheetRepository
{
    /// <summary>The sheet with its lines (and their job rows) and chosen employees, tracked.</summary>
    Task<Payroll?> GetForEditAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The sheets that are not Cancelled whose period shares a day with <paramref name="from"/>…<paramref name="to"/>, with their lines and chosen employees, untracked.</summary>
    Task<IReadOnlyList<Payroll>> ListActiveOverlappingAsync(DateOnly from, DateOnly to, Guid? exceptId, CancellationToken cancellationToken);

    void Add(Payroll payroll);

    /// <summary>Adds a payslip or job row to a sheet that is already tracked: its key is set, so it must be added explicitly or it would be read as an existing row.</summary>
    void Add(PayrollLine line);

    void Add(PayrollLineJob job);

    /// <summary>Removes the payslip of a sheet that is still Provisional, with its job rows.</summary>
    void Remove(PayrollLine line);

    void Remove(PayrollLineJob job);
}

/// <summary>What a payroll calculation reads for a period (BR-LABOR-10): everything is untracked.</summary>
/// <param name="Employees">With type periods; the employees with attendance in the period, or the ones asked for.</param>
/// <param name="Attendances">Of those employees in the period.</param>
/// <param name="Assignments">The job periods of those employees in the period, with their job type.</param>
/// <param name="Settings">Every version of the attendance settings.</param>
/// <param name="JobTypes">The job types of the assignments, with their rates.</param>
public sealed record PayrollWorkData(
    IReadOnlyList<Employee> Employees,
    IReadOnlyList<Attendance> Attendances,
    IReadOnlyList<WorkAssignment> Assignments,
    IReadOnlyList<AttendanceSettingsVersion> Settings,
    IReadOnlyDictionary<Guid, JobType> JobTypes);

/// <summary>The records a payroll is calculated from, and the requests that stop it being finalized.</summary>
public interface IPayrollWorkReader
{
    /// <summary>The work of the period. <paramref name="employeeIds"/> null means every employee with attendance in it.</summary>
    Task<PayrollWorkData> LoadAsync(DateOnly from, DateOnly to, IReadOnlyCollection<Guid>? employeeIds, CancellationToken cancellationToken);

    /// <summary>The employees with these ids, with type periods, untracked.</summary>
    Task<IReadOnlyList<Employee>> GetEmployeesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>The requests Awaiting review that have a day in the period, with their employee; of these employees only, or of everyone when null.</summary>
    Task<IReadOnlyList<EmployeeRequest>> ListAwaitingRequestsAsync(DateOnly from, DateOnly to, IReadOnlyCollection<Guid>? employeeIds, CancellationToken cancellationToken);
}

/// <summary>An advance not fully deducted yet, tracked, with what is left of it.</summary>
public sealed record OpenAdvance(WagePayment Advance, decimal Remaining);

/// <summary>The wage payments the services change (LABOR-14).</summary>
public interface IWagePaymentRepository
{
    /// <summary>The payslips of the employee in Finalized sheets, the oldest period first, with their sheet, tracked.</summary>
    Task<IReadOnlyList<PayrollLine>> ListFinalizedLinesAsync(Guid employeeId, CancellationToken cancellationToken);

    /// <summary>What is paid on each of these payslips: their wage payments whose voucher is not cancelled.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> PaidAmountsAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken);

    /// <summary>The advances of these employees not cancelled and not fully deducted, oldest first, tracked.</summary>
    Task<IReadOnlyList<OpenAdvance>> ListOpenAdvancesAsync(IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken);

    /// <summary>The payments saved with this request key, in order, with their voucher.</summary>
    Task<IReadOnlyList<WagePayment>> ListByRequestKeyAsync(string key, CancellationToken cancellationToken);

    void Add(WagePayment payment);

    void Add(PayrollAdvanceDeduction deduction);
}

public sealed record PayrollHeaderRow(Guid Id, string Code, PayrollPeriodType PeriodType, DateOnly From, DateOnly To, int StatusId, DateTimeOffset CreatedAt, decimal Total, decimal Paid);

public sealed record PayrollSlipRaw(
    Guid Id, string Code, Guid EmployeeId, string? EmployeeCode, string EmployeeName, decimal Hours, string? HourlyRateNote,
    decimal MainPay, decimal Bonus, decimal Deduction, decimal Paid);

public sealed record PayrollDetailRaw(
    Guid Id, string Code, PayrollPeriodType PeriodType, DateOnly From, DateOnly To, PayrollScope Scope, int StatusId, string? ExpenseMonth,
    string? Note, Guid? CreatedBy, DateTimeOffset CreatedAt, IReadOnlyList<PayrollSlipRaw> Slips);

public sealed record SlipJobRaw(string JobName, decimal Hours, decimal HourlyRate, decimal Amount);

public sealed record SlipPaymentRaw(Guid Id, string VoucherCode, DateTimeOffset PaidAt, PaymentMethod Method, decimal Amount, WagePaymentKind Kind, bool Cancelled);

public sealed record SlipDetailRaw(
    PayrollSlipRaw Slip, Guid PayrollId, string PayrollCode, DateOnly From, DateOnly To, int StatusId,
    IReadOnlyList<SlipJobRaw> Jobs, IReadOnlyList<SlipPaymentRaw> Payments);

public sealed record EmployeeSlipRaw(
    Guid Id, string Code, Guid PayrollId, string PayrollCode, PayrollPeriodType PeriodType, DateOnly From, DateOnly To, int StatusId,
    decimal Income, decimal Net, decimal Paid);

public sealed record EmployeeSlipPageRaw(IReadOnlyList<EmployeeSlipRaw> Items, int Total, decimal Outstanding, decimal AdvanceBalance);

public sealed record PaymentRaw(
    Guid Id, string VoucherCode, DateTimeOffset PaidAt, Guid EmployeeId, string EmployeeName, Guid? PayrollId, string? PayrollCode,
    PaymentMethod Method, decimal Amount, WagePaymentKind Kind, bool Cancelled);

public sealed record PayableEmployeeRaw(Guid EmployeeId, string? Code, string Name, decimal Outstanding);

/// <summary>The read side of the payroll screens: sums are taken in the database, and a payment counts as paid while its voucher is not cancelled.</summary>
public interface IPayrollReader
{
    /// <summary>Every sheet with a status among <paramref name="statusIds"/> (all when null), of the type, whose period shares a day with <paramref name="from"/>…<paramref name="to"/>, with its total (sum of net pay) and paid.</summary>
    Task<IReadOnlyList<PayrollHeaderRow>> ListHeadersAsync(
        IReadOnlyCollection<int>? statusIds, PayrollPeriodType? type, DateOnly? from, DateOnly? to, CancellationToken cancellationToken);

    Task<PayrollDetailRaw?> GetDetailAsync(Guid id, CancellationToken cancellationToken);

    Task<SlipDetailRaw?> GetSlipAsync(Guid slipId, CancellationToken cancellationToken);

    /// <summary>The payslips of the employee, the newest period first; <paramref name="statusIds"/> null leaves out Cancelled sheets.</summary>
    Task<EmployeeSlipPageRaw> ListEmployeeSlipsAsync(Guid employeeId, IReadOnlyCollection<int>? statusIds, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The payments, newest first, matching the code of the voucher, the employee's name or the code of the sheet.</summary>
    Task<(IReadOnlyList<PaymentRaw> Items, int Total)> ListPaymentsAsync(
        string? search, Guid? employeeId, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Employees with something owed on Finalized sheets (Left ones too), or every Employed employee when <paramref name="includeAll"/>; by name, at most <paramref name="take"/>.</summary>
    Task<IReadOnlyList<PayableEmployeeRaw>> ListPayableEmployeesAsync(string? search, bool includeAll, int take, CancellationToken cancellationToken);
}
