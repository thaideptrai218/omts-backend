using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.EmployeeApp;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees;

/// <summary>
/// An employee is Left from the day after the last working day (BR-LABOR-01): the daily job flips those whose last working day
/// has passed, and removes the work assignments after that day through <see cref="IEmployeeWorkRecords"/>. An automatic
/// action, so the audit row has no person (GBR-02).
/// </summary>
public sealed class EmploymentEndService(
    IEmployeeRepository employees, IEmployeeWorkRecords workRecords, EmployeeAppAccess employeeApp, AuditLogService audit, IUnitOfWork unitOfWork)
{
    public Task<IReadOnlyList<Guid>> DueIdsAsync(DateOnly today, CancellationToken cancellationToken) =>
        employees.ListEmployedWithLastWorkingDayBeforeAsync(today, cancellationToken);

    /// <summary>Whether the employee became Left; false when someone resumed or changed them meanwhile.</summary>
    public async Task<bool> EndAsync(Guid employeeId, DateOnly today, CancellationToken cancellationToken)
    {
        if (await employees.GetByIdAsync(employeeId, cancellationToken) is not { StatusId: StatusIds.Employee.Working, LastWorkingDay: { } lastDay } employee || lastDay >= today)
        {
            return false;
        }

        // LABOR-02 (L4): an employee ended today stays Employed until now and can be assigned meanwhile, so this is the last
        // moment to remove those assignments. L4 fills the port; it needs no change here.
        await workRecords.RemoveAssignmentsAfterAsync(employee.Id, lastDay, cancellationToken);
        employee.StatusId = StatusIds.Employee.Left;
        audit.RecordStatusChange(nameof(Employee), employee.Id, null, StatusIds.Employee.Working, StatusIds.Employee.Left, $"Last working day {lastDay:yyyy-MM-dd}");
        // Left logs the employee out of the employee app and unlinks the device (BR-LABOR-02), in the same save. The app already refused
        // them from the day after the last working day, whether or not this job had run.
        await employeeApp.RevokeAsync(employee.Id, EmployeeDeviceUnlinkReason.EmploymentEnded, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
