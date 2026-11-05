using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Labor;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Employees;

/// <summary>
/// LABOR-01 step 4: the fields, then what they point to. A zone or job type is checked only when it is new on the profile,
/// so one discontinued later does not stop other edits (GBR-01).
/// </summary>
public sealed class EmployeeProfileValidator(IEmployeeRepository employees, IFarmingZoneRepository zones, PayPeriodLock payPeriods, TimeProvider timeProvider)
{
    /// <param name="employee">The profile being edited; null when adding.</param>
    public async Task<Result<EmployeeInput>> ValidateAsync(
        SaveEmployeeRequest request, Employee? employee, EmployeeCaller caller, CancellationToken cancellationToken)
    {
        var (input, invalid) = EmployeeInput.From(request, timeProvider.Today(), adding: employee is null);
        if (input is null)
        {
            return AppError.InvalidFields(invalid);
        }

        var newZones = input.WorkingZoneIds.Where(z => employee is null || employee.WorkingZones.All(w => w.ZoneId != z)).ToList();
        if (newZones.Any(z => !caller.HasZone(z)))
        {
            return AppError.Forbidden;
        }

        var zoneRows = await zones.GetByIdsAsync(newZones, cancellationToken);
        if (zoneRows.Count != newZones.Count || zoneRows.Any(z => z.StatusId != StatusIds.Usage.InUse))
        {
            return AppError.Invalid("Working zones");
        }

        var newJobTypes = input.JobTypeIds.Where(j => employee is null || employee.JobTypes.All(e => e.JobTypeId != j)).ToList();
        var jobTypeRows = await employees.GetJobTypesAsync(newJobTypes, cancellationToken);
        if (jobTypeRows.Count != newJobTypes.Count || jobTypeRows.Any(j => j.StatusId != StatusIds.Usage.InUse))
        {
            return AppError.Invalid("Job types");
        }

        if (employee is not null && EditRefusal(employee, input, caller) is { } refused)
        {
            return refused;
        }

        if (employee is not null && await TypeDateRefusalAsync(employee, input, cancellationToken) is { } locked)
        {
            return locked;
        }

        return await PhoneRefusalAsync(input.PhoneNumber, employee?.Id, cancellationToken) is { } taken ? taken : input;
    }

    /// <summary>
    /// LABOR-01.1.E1: a new type takes effect after every pay period that is sent or finalized (MSG-LABOR-04). Only on an edit:
    /// the first type of a new employee changes no day, as the days before it have that same type and no attendance.
    /// </summary>
    private async Task<AppError?> TypeDateRefusalAsync(Employee employee, EmployeeInput input, CancellationToken cancellationToken) =>
        input is { Type: { } type, TypeFrom: { } from } && EmployeeProfileChanges.IsTypeChange(employee, type, from)
            ? await payPeriods.CheckAsync(from, cancellationToken)
            : null;

    /// <summary>
    /// An edit keeps at least one working zone. A new type needs its date; the type in force sent back without a date changes
    /// nothing (LABOR-01.1 step 2).
    /// </summary>
    private AppError? EditRefusal(Employee employee, EmployeeInput input, EmployeeCaller caller)
    {
        if (EmployeeProfileChanges.ZonesAfterEdit(employee, input.WorkingZoneIds, caller).Count == 0)
        {
            return AppError.Invalid("Working zones");
        }

        return input is { Type: { } type, TypeFrom: null } && type != EmployeeTypePeriod.TypeOn(employee.TypePeriods, timeProvider.Today())
            ? AppError.Invalid("Employee type date")
            : null;
    }

    /// <summary>MSG-LABOR-03 naming the other Employed employee who has the number, with their id so the client can open them.</summary>
    public async Task<AppError?> PhoneRefusalAsync(string? phone, Guid? exceptEmployeeId, CancellationToken cancellationToken) =>
        phone is not null && await employees.FindEmployedWithPhoneAsync(phone, exceptEmployeeId, cancellationToken) is { } other
            ? AppError.Conflict(UserMessages.PhoneBelongsToEmployee(other.FullName), new Dictionary<string, object?> { ["existingEmployeeId"] = other.Id })
            : null;
}
