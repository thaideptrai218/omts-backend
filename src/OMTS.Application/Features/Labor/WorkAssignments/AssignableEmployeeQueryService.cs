using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Labor.WorkAssignments;

/// <summary>
/// LABOR-02.0 steps 5 and 6: the Employed employees who have a working zone among the caller's zones, those whose profile has the
/// job type first, each with the raft of their assignment on the date. A search by name or phone number looks at every Employed employee
/// of the cooperative (BR-LABOR-01). Staff employees are listed too: they may be assigned to a raft, and their hours then have no cost (design Q17).
/// </summary>
public sealed class AssignableEmployeeQueryService(IWorkAssignmentRepository assignments, IAttendanceDayReader attendance, ZoneAccessGuard zoneAccess)
{
    private const int Limit = 200;

    /// <summary>The longest name the column holds: a longer text can match no name or number.</summary>
    private const int MaxSearchLength = 200;

    /// <param name="raftId">The raft the work is assigned on, only to check that the caller works in its zone.</param>
    public async Task<Result<AssignableEmployees>> ListAsync(DateOnly date, Guid? raftId, Guid? jobTypeId, string? search, CancellationToken cancellationToken)
    {
        var zones = await zoneAccess.PermittedZonesAsync(PermissionFunctions.WorkAssignments, PermissionAction.View, cancellationToken);
        if (zones.IsEmpty)
        {
            return AppError.Forbidden;
        }

        if (raftId is { } id)
        {
            if (await assignments.GetRaftAsync(id, cancellationToken) is not { } raft)
            {
                return AppError.NotFound;
            }

            if (!zones.Allows(raft.ZoneId))
            {
                return AppError.Forbidden;
            }
        }

        var text = search?.Trim();
        if (text?.Length > MaxSearchLength)
        {
            return new AssignableEmployees(date, [], false);
        }

        var employees = await assignments.ListEmployedAsync(
            string.IsNullOrEmpty(text) ? zones : null, text, PhoneNumbers.Normalize(text), jobTypeId, Limit + 1, cancellationToken);
        var shown = employees.Take(Limit).ToList();
        var ids = shown.Select(e => e.Id).ToList();
        var assigned = (await assignments.ListOfEmployeesOnAsync(ids, date, tracked: false, cancellationToken))
            .Where(p => p.IsFirstPeriod)
            .ToDictionary(p => p.EmployeeId);
        var days = await attendance.ReadAsync(date, ids, cancellationToken);

        var items = shown.Select(e => new AssignableEmployee(
            e.Id,
            e.FullName,
            e.PhoneNumber,
            EmployeeTypePeriod.TypeOn(e.TypePeriods, date),
            zones.AllZones || e.WorkingZones.Any(w => zones.ZoneIds.Contains(w.ZoneId)),
            jobTypeId is { } job && e.JobTypes.Any(j => j.JobTypeId == job),
            days.GetValueOrDefault(e.Id) is { IsCheckedIn: true },
            assigned.GetValueOrDefault(e.Id) is { } a ? new AssignableAssignment(a.Id, a.RaftId, a.Raft.Code, a.JobType.Name) : null)).ToList();
        return new AssignableEmployees(date, items, employees.Count > Limit);
    }
}
