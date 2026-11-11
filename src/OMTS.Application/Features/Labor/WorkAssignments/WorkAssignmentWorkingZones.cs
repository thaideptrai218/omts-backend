using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Labor.WorkAssignments;

/// <summary>
/// LABOR-02 step 9 (BR-LABOR-01): work on a raft gives the employee the zone of that raft as a working zone. Shared by the assign and
/// the edit of an assignment, so a move to a raft of another zone ends the same way in both.
/// </summary>
public sealed class WorkAssignmentWorkingZones(IEmployeeRepository employees, AuditLogService audit, IUnitOfWork unitOfWork)
{
    /// <summary>Adds the zone of the raft when the employee (with working zones loaded) lacks it; the row version of the employee moves with it.</summary>
    public void Ensure(Employee employee, Raft raft)
    {
        if (employee.WorkingZones.Any(w => w.ZoneId == raft.ZoneId))
        {
            return;
        }

        employees.AddWorkingZone(new EmployeeWorkingZone { EmployeeId = employee.Id, ZoneId = raft.ZoneId });
        unitOfWork.MarkChanged(employee);
        audit.Record(new AuditEntry("Employee.WorkingZoneAdded", nameof(Employee)) { RecordId = employee.Id, ZoneId = raft.ZoneId, After = new { raft.ZoneId } });
    }
}
