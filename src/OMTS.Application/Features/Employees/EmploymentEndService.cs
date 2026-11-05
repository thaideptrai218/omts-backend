using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Employees;

/// <summary>
/// An employee is Left from the day after the last working day (BR-LABOR-01): the daily job flips those whose last working day
/// has passed. An automatic action, so the audit row has no person (GBR-02).
/// </summary>
public sealed class EmploymentEndService(IEmployeeRepository employees, AuditLogService audit, IUnitOfWork unitOfWork)
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

        employee.StatusId = StatusIds.Employee.Left;
        audit.RecordStatusChange(nameof(Employee), employee.Id, null, StatusIds.Employee.Working, StatusIds.Employee.Left, $"Last working day {lastDay:yyyy-MM-dd}");
        // LABOR-03 and LABOR-06 (L3): Left logs the employee out and unlinks the device (BR-LABOR-02), here.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
