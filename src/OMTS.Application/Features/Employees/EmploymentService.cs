using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.EmployeeApp;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees;

/// <summary>
/// LABOR-01.3 end employment and LABOR-01.4 resume employment. Each save moves the row version of the employee, so a stale
/// screen or a parallel save answers MSG06.
/// </summary>
public sealed class EmploymentService(
    IEmployeeRepository employees,
    IEmployeeWorkRecords workRecords,
    IAccountRepository accounts,
    EmployeeAppAccess employeeApp,
    EmployeeCallerAccess callers,
    EmployeeProfileValidator validator,
    EmployeeQueryService queries,
    INotificationDispatcher notifications,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>
    /// The last working day is today or earlier and not before a day with attendance (LABOR-01.3.E1). A day before today makes
    /// the employee Left at once; today leaves them Employed until <c>EmploymentEndService</c> runs on the next day.
    /// </summary>
    public async Task<Result<EmploymentChanged>> EndEmploymentAsync(Guid id, EndEmploymentRequest request, CancellationToken cancellationToken)
    {
        var employee = await employees.GetForEditAsync(id, cancellationToken);
        if (employee is null)
        {
            return AppError.NotFound;
        }

        var caller = await callers.GetAsync(cancellationToken);
        if (!caller.Maintains(employee))
        {
            return AppError.Forbidden;
        }

        var today = timeProvider.Today();
        if (request.LastWorkingDay is not { } lastDay || lastDay > today)
        {
            return AppError.Invalid("Last working day");
        }

        if (await rowVersions.CheckAsync(employee, request.RowVersion, cancellationToken) is { } stale)
        {
            return stale;
        }

        if (employee.StatusId != StatusIds.Employee.Working)
        {
            return WrongStatus(employee);
        }

        if (await workRecords.LastAttendanceDayAsync(employee.Id, cancellationToken) is { } lastAttendance && lastAttendance > lastDay)
        {
            return AppError.Invalid("Last working day");
        }

        await workRecords.RemoveAssignmentsAfterAsync(employee.Id, lastDay, cancellationToken);
        var previous = employee.LastWorkingDay;
        employee.LastWorkingDay = lastDay;
        audit.Record(new AuditEntry("Employee.EmploymentEnded", nameof(Employee))
        {
            RecordId = employee.Id, Before = new { LastWorkingDay = previous }, After = new { LastWorkingDay = lastDay },
        });
        if (lastDay < today)
        {
            employee.StatusId = StatusIds.Employee.Left;
            audit.RecordStatusChange(nameof(Employee), employee.Id, null, StatusIds.Employee.Working, StatusIds.Employee.Left, $"Last working day {lastDay:yyyy-MM-dd}");
            // Left logs the employee out of the employee app and unlinks the device (BR-LABOR-02), in this save; for a last working day of
            // today, EmploymentEndService does the same on the next day.
            await employeeApp.RevokeAsync(employee.Id, EmployeeDeviceUnlinkReason.EmploymentEnded, cancellationToken);
        }

        // One reminder for one end of employment (LABOR-01.3 step 5): moving the day sends no second one, and a notification
        // keeps the content it was sent with (BR-ACCOUNT-12). A resume clears the day, so a later end reminds again.
        if (previous is null)
        {
            await RemindToLockStaffAccountAsync(employee, lastDay, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new EmploymentChanged(await queries.ViewAsync(employee, caller, cancellationToken));
    }

    /// <summary>Left → Employed from today, without a new approval, when no Employed employee has the phone number (LABOR-01.4.E1).</summary>
    public async Task<Result<EmploymentChanged>> ResumeAsync(Guid id, ResumeEmployeeRequest request, CancellationToken cancellationToken)
    {
        var employee = await employees.GetForEditAsync(id, cancellationToken);
        if (employee is null)
        {
            return AppError.NotFound;
        }

        var caller = await callers.GetAsync(cancellationToken);
        if (!caller.Maintains(employee))
        {
            return AppError.Forbidden;
        }

        if (!EmployeeInput.TryPhone(request.PhoneNumber, out var phone))
        {
            return AppError.Invalid("Phone number");
        }

        if (await rowVersions.CheckAsync(employee, request.RowVersion, cancellationToken) is { } stale)
        {
            return stale;
        }

        if (employee.StatusId != StatusIds.Employee.Left)
        {
            return WrongStatus(employee);
        }

        var newPhone = request.PhoneNumber is null ? employee.PhoneNumber : phone;
        if (await validator.PhoneRefusalAsync(newPhone, employee.Id, cancellationToken) is { } taken)
        {
            return taken;
        }

        var before = new { employee.PhoneNumber, employee.LastWorkingDay };
        employee.PhoneNumber = newPhone;
        employee.LastWorkingDay = null;
        employee.StatusId = StatusIds.Employee.Working;
        audit.RecordStatusChange(nameof(Employee), employee.Id, null, StatusIds.Employee.Left, StatusIds.Employee.Working);
        audit.Record(new AuditEntry("Employee.EmploymentResumed", nameof(Employee))
        {
            RecordId = employee.Id, Before = before, After = new { employee.PhoneNumber, employee.LastWorkingDay },
        });
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            // A parallel save took the number between the check and this save (unique index): answer as the check would.
            if (await validator.PhoneRefusalAsync(newPhone, employee.Id, cancellationToken) is { } takenMeanwhile)
            {
                return takenMeanwhile;
            }

            throw;
        }

        return new EmploymentChanged(await queries.ViewAsync(employee, caller, cancellationToken));
    }

    /// <summary>MSG-LABOR-02 when an Active staff account has the phone number of the employee: kept in the Owners' list, never pushed (BR-LABOR-04).</summary>
    private async Task RemindToLockStaffAccountAsync(Employee employee, DateOnly lastDay, CancellationToken cancellationToken)
    {
        if (employee.PhoneNumber is not { } phone
            || await accounts.FindByPhoneAsync(phone, cancellationToken) is not { SubjectType: SubjectType.Staff, StatusId: StatusIds.Account.Active } account)
        {
            return;
        }

        await notifications.NotifyAsync(
            await accounts.ListActiveOwnerIdsAsync(cancellationToken),
            new NotificationContent(
                NotificationTypes.StaffAccountLockReminder,
                "Reminder to lock a staff account",
                UserMessages.LockStaffAccountReminder(employee.FullName, lastDay, account.Username),
                new RelatedRecord(RelatedRecordTypes.Account, account.Id, account.Username)),
            cancellationToken);
    }

    private AppError WrongStatus(Employee employee) =>
        AppError.Conflict(UserMessages.WrongStatus("Employee", statusCatalog.DisplayNameOf(employee.StatusId)));
}
