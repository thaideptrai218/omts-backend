using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Employees;

/// <summary>LABOR-01.2: a Zone Manager adds one of their zones to any Employed employee, and then maintains them (BR-LABOR-01).</summary>
public sealed class EmployeeWorkingZoneService(
    IEmployeeRepository employees,
    IFarmingZoneRepository zones,
    EmployeeCallerAccess callers,
    EmployeeQueryService queries,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    /// <summary>A zone the employee already has changes nothing; added by a parallel save, it answers MSG06. The save moves the row version of the employee, so it meets a parallel end of employment.</summary>
    public async Task<Result<EmployeeSaved>> AddAsync(Guid id, AddWorkingZoneRequest request, CancellationToken cancellationToken)
    {
        if (request.ZoneId is not { } zoneId)
        {
            return AppError.Invalid("Working zones");
        }

        var employee = await employees.GetForEditAsync(id, cancellationToken);
        if (employee is null)
        {
            return AppError.NotFound;
        }

        var caller = await callers.GetAsync(cancellationToken);
        if (!caller.HasZone(zoneId))
        {
            return AppError.Forbidden;
        }

        if (employee.StatusId != StatusIds.Employee.Working)
        {
            return AppError.Conflict(UserMessages.WrongStatus("Employee", statusCatalog.DisplayNameOf(employee.StatusId)));
        }

        if (employee.WorkingZones.All(w => w.ZoneId != zoneId))
        {
            if (await zones.GetByIdsAsync([zoneId], cancellationToken) is not [{ StatusId: StatusIds.Usage.InUse }])
            {
                return AppError.Invalid("Working zones");
            }

            employees.AddWorkingZone(new EmployeeWorkingZone { EmployeeId = employee.Id, ZoneId = zoneId });
            unitOfWork.MarkChanged(employee);
            audit.Record(new AuditEntry("Employee.WorkingZoneAdded", nameof(Employee)) { RecordId = employee.Id, ZoneId = zoneId, After = new { ZoneId = zoneId } });
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (DuplicateRecordException)
            {
                // A parallel save added this zone: the insert meets the key of EmployeeWorkingZones before the version check.
                return AppError.Conflict(UserMessages.ConcurrentUpdateByAnotherUser);
            }
        }

        var saved = UserMessages.Saved("Employee profile");
        var view = await queries.ViewAsync((await employees.GetForEditAsync(id, cancellationToken))!, caller, cancellationToken);
        return new EmployeeSaved(view, saved.Text, saved.Code);
    }
}
