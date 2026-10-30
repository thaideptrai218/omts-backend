using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Labor;
namespace OMTS.Application.Features.Workforce;

public sealed class LeaveService(IWorkforceRepository repository, ICurrentUser user, IAccountRepository accounts,
    AuditLogService audit, RowVersionGuard versions, IUnitOfWork uow, INotificationDispatcher notifications, TimeProvider clock)
{
    public async Task<IReadOnlyList<LeaveRequestView>> ListAsync(CancellationToken ct) =>
        (await repository.LeaveAsync(null, ct)).Select(View).ToList();

    public async Task<Result<LeaveRequestView>> RequestAsync(RequestLeave input, CancellationToken ct)
    {
        var employee = user.AccountId is { } account ? await repository.MyEmployeeAsync(account, ct) : null;
        if (employee is null) return AppError.Forbidden;
        if (input.From == default || input.To < input.From ||
            string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 500 || input.ClientOperationId == Guid.Empty)
            return AppError.Invalid("Leave request");
        if (await repository.LeaveOperationAsync(employee.Id, input.ClientOperationId, ct) is { } existing) return View(existing);
        if (await repository.HasLockedPayrollPeriodAsync(input.From, input.To, ct)) return AppError.Invalid("Locked payroll period");
        var pending = (await repository.LeaveAsync(employee.Id, ct)).FirstOrDefault(r =>
            r.ReviewedAt is null && r.CancelledAt is null && r.From <= input.To && r.To >= input.From);
        if (pending is not null) return AppError.AlreadyExists("Leave request", pending.Id);
        var row = new EmployeeLeaveRequest { EmployeeId = employee.Id, Employee = employee, From = input.From, To = input.To,
            Reason = input.Reason.Trim(), ClientOperationId = input.ClientOperationId };
        repository.AddLeave(row);
        // Serialize competing requests for this employee with its row version, so two overlapping
        // pending requests cannot both commit after reading the same empty list (BR-LABOR-08).
        uow.MarkChanged(employee);
        audit.Record(new AuditEntry("EmployeeLeave.Requested", nameof(EmployeeLeaveRequest)) { RecordId = row.Id, After = new { row.EmployeeId, row.From, row.To, row.Reason } });
        await notifications.NotifyAsync(await accounts.ListActiveOwnerIdsAsync(ct), "EMPLOYEE_LEAVE_REQUESTED", "Yêu cầu nghỉ phép",
            $"{employee.FullName}: {row.From:dd/MM/yyyy} – {row.To:dd/MM/yyyy}", "EmployeeLeaveRequest", row.Id.ToString(), false, ct);
        await uow.SaveChangesAsync(ct);
        return View(row);
    }

    public async Task<Result<LeaveRequestView>> ReviewAsync(Guid id, ReviewLeave input, CancellationToken ct)
    {
        var row = await repository.LeaveRequestAsync(id, ct);
        if (row is null) return AppError.NotFound;
        if (row.ReviewedAt is not null || row.CancelledAt is not null) return AppError.AlreadyExists("Reviewed leave request", row.Id);
        if (await versions.CheckAsync(row, input.RowVersion, ct) is { } error) return error;
        if ((!input.Approved && string.IsNullOrWhiteSpace(input.Response)) || input.Response?.Length > 500) return AppError.Invalid("Response");
        if (input.Approved && await repository.HasLockedPayrollPeriodAsync(row.From, row.To, ct)) return AppError.Invalid("Locked payroll period");
        // Approval is a leave decision only: it never invents an attendance check-in or deletes assignments.
        row.Approved = input.Approved; row.Response = input.Response?.Trim();
        row.ReviewedAt = clock.GetUtcNow(); row.ReviewedByAccountId = user.AccountId;
        audit.Record(new AuditEntry("EmployeeLeave.Reviewed", nameof(EmployeeLeaveRequest))
        { RecordId = row.Id, After = new { row.Approved, row.Response } });
        if (row.Employee.AccountId is { } accountId)
            await notifications.NotifyAsync([accountId], "EMPLOYEE_LEAVE_REVIEWED", "Phản hồi yêu cầu nghỉ phép", row.Response, "EmployeeLeaveRequest", row.Id.ToString(), false, ct);
        await uow.SaveChangesAsync(ct);
        return View(row);
    }

    public static LeaveRequestView View(EmployeeLeaveRequest r) =>
        new(r.Id, r.Employee.FullName, r.From, r.To, r.Reason, r.Approved, r.Response, RowVersionGuard.Encode(r.RowVersion), r.CancelledAt);

    public async Task<Result<LeaveRequestView>> CancelAsync(Guid id, CancellationToken ct)
    {
        var employee = user.AccountId is { } accountId ? await repository.MyEmployeeAsync(accountId, ct) : null;
        if (employee is null) return AppError.Forbidden;
        var row = await repository.LeaveRequestAsync(id, ct);
        if (row is null) return AppError.NotFound;
        if (row.EmployeeId != employee.Id) return AppError.Forbidden;
        if (row.ReviewedAt is not null) return AppError.AlreadyExists("Reviewed leave request", row.Id);
        if (row.CancelledAt is not null) return View(row);
        row.CancelledAt = clock.GetUtcNow();
        audit.Record(new AuditEntry("EmployeeLeave.Cancelled", nameof(EmployeeLeaveRequest)) { RecordId = row.Id });
        await uow.SaveChangesAsync(ct);
        return View(row);
    }
}
