using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Harvest;

/// <summary>An employee on a harvest trip.</summary>
/// <param name="Code">The employee code (NV000001); null for a row older than the column.</param>
public sealed record HarvestCrewMemberView(Guid Id, string? Code, string Name);

/// <summary>The employees assigned to the raft on a day (LABOR-02), offered as the crew of a harvest there; any other employee can still be chosen.</summary>
public sealed record HarvestCrewSuggestions(Guid RaftId, string RaftCode, DateOnly Date, IReadOnlyList<HarvestCrewMemberView> Employees);

/// <summary>
/// The crew of a harvest lot (decision of 12/11/2026): an optional list of employees, for the record only. Each must exist, work in the cooperative and still be employed. One employee may
/// be on the lots of several rafts on the same day, and no quota or pay is counted per person. Sent as one <c>crew</c> form field per employee id.
/// </summary>
public sealed class HarvestCrewService(
    IEmployeeRepository employees,
    ICooperativeRepository cooperatives,
    IWorkAssignmentRepository assignments,
    IHarvestLotRepository lots,
    IRaftRepository rafts,
    ZoneAccessGuard zoneAccess,
    TimeProvider timeProvider)
{
    public const string CrewField = "Crew";

    /// <summary>A trip rarely has more hands than this: it keeps a typing error from filling a lot with the whole staff.</summary>
    public const int MaxCrew = 100;

    /// <summary>
    /// The employees of the request, each once. <paramref name="requested"/> is null when the field was not sent (nothing to check: <c>Employees</c> is null); an unreadable id, an unknown
    /// employee, one who left, one of another cooperative, or more than <see cref="MaxCrew"/> is MSG-GEN-01 on "Crew".
    /// </summary>
    public async Task<(IReadOnlyList<Employee>? Employees, AppError? Error)> ReadAsync(IReadOnlyList<Guid>? requested, bool unreadable, CancellationToken cancellationToken)
    {
        if (unreadable)
        {
            return (null, AppError.Invalid(CrewField, "Send each employee id as a Crew field."));
        }

        if (requested is null)
        {
            return (null, null);
        }

        var ids = requested.Distinct().ToList();
        if (ids.Count > MaxCrew)
        {
            return (null, AppError.Invalid(CrewField, $"A harvest lists at most {MaxCrew} employees."));
        }

        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken);
        var found = new List<Employee>(ids.Count);
        foreach (var id in ids)
        {
            var employee = await employees.GetByIdAsync(id, cancellationToken);
            if (employee is null || employee.CooperativeId != cooperative?.Id || employee.StatusId != StatusIds.Employee.Working)
            {
                return (null, AppError.Invalid(CrewField, "Choose employees of the cooperative who still work here."));
            }

            found.Add(employee);
        }

        return (found, null);
    }

    /// <summary>Makes the crew of the lot the given employees: rows no longer wanted go, new ones come, the others stay. Returns whether anything changed.</summary>
    public bool Apply(HarvestLot lot, IReadOnlyList<Employee> wanted)
    {
        var wantedIds = wanted.Select(e => e.Id).ToHashSet();
        var changed = false;
        foreach (var member in lot.Crew.Where(m => !wantedIds.Contains(m.EmployeeId)).ToList())
        {
            lot.Crew.Remove(member);
            lots.RemoveCrew(member);
            changed = true;
        }

        foreach (var employee in wanted.Where(e => lot.Crew.All(m => m.EmployeeId != e.Id)))
        {
            var member = new HarvestLotCrewMember { LotId = lot.Id, EmployeeId = employee.Id, Employee = employee };
            lot.Crew.Add(member);
            lots.AddCrew(member);
            changed = true;
        }

        return changed;
    }

    public static IReadOnlyList<HarvestCrewMemberView> ViewOf(HarvestLot lot) => lot.Crew
        .Where(m => m.Employee is not null)
        .Select(m => new HarvestCrewMemberView(m.EmployeeId, m.Employee.Code, m.Employee.FullName))
        .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.Code, StringComparer.Ordinal)
        .ToList();

    /// <summary>The names of the crew for the audit log, in name order.</summary>
    public static IReadOnlyList<string> AuditNames(HarvestLot lot) => lot.Crew
        .Where(m => m.Employee is not null)
        .Select(m => m.Employee.Code is { Length: > 0 } code ? $"{code} {m.Employee.FullName}" : m.Employee.FullName)
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// The employees assigned to the raft on the day (default today): who the Zone Manager would put on the crew first. Read only, for those who may view harvests of the raft's zone.
    /// </summary>
    public async Task<Result<HarvestCrewSuggestions>> SuggestAsync(Guid raftId, DateOnly? date, CancellationToken cancellationToken)
    {
        var raft = (await rafts.GetByIdsAsync([raftId], cancellationToken)).FirstOrDefault();
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.Harvests, PermissionAction.View, raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var day = date ?? timeProvider.Today();
        var onRaft = (await assignments.ListDayAsync(day, cancellationToken))
            .Where(a => a.RaftId == raftId && a.Employee.StatusId == StatusIds.Employee.Working)
            .Select(a => a.Employee)
            .DistinctBy(e => e.Id)
            .Select(e => new HarvestCrewMemberView(e.Id, e.Code, e.FullName))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new HarvestCrewSuggestions(raft.Id, raft.Code, day, onRaft);
    }
}
