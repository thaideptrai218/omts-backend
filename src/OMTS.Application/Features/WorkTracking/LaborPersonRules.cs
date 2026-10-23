using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Workforce;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.WorkTracking;

/// <summary>Rules on the people of LAB-03/LAB-04 shared by work assignment and attendance.</summary>
public static class LaborPersonRules
{
    /// <summary>
    /// MSG72 when the employee is Left and the end date is on or before the day: no work is assigned and no
    /// attendance is recorded from the end date onward.
    /// </summary>
    public static AppError? LeftOn(Employee employee, DateOnly day) =>
        employee.StatusId == StatusIds.Employee.Left && employee.EmploymentEndDate is { } end && end <= day
            ? AppError.Conflict(
                UserMessages.EmployeeHasLeft(employee.FullName, LaborInputRules.Date(end)),
                new Dictionary<string, object?> { ["employeeId"] = employee.Id, ["employmentEndDate"] = end })
            : null;

    /// <summary>
    /// The employee of a zone's work or attendance: MSG72 for a Left employee on or after the end date, otherwise 400
    /// MSG01 "Employee" unless the employee is Working (or still employed on that day, i.e. Left later) and has the zone
    /// among their working zones (the pickers already list only those).
    /// </summary>
    public static AppError? CheckForZone(Employee employee, Guid zoneId, DateOnly day)
    {
        if (LeftOn(employee, day) is { } left)
        {
            return left;
        }

        var employed = employee.StatusId == StatusIds.Employee.Working
            || (employee.StatusId == StatusIds.Employee.Left && employee.EmploymentEndDate > day);
        return employed && employee.WorkingZones.Any(w => w.ZoneId == zoneId) ? null : AppError.Invalid("Employee");
    }
}
