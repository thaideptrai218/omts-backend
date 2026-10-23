using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.LaborCost;
using OMTS.Application.Features.Workforce;
using OMTS.Application.Features.Workforce.Dtos;
using OMTS.Application.Features.WorkTracking.Dtos;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.WorkTracking;

/// <summary>
/// LAB-04 sheet of one zone and day: saved records plus rows marked <c>suggested</c> from the active daily-wage
/// assignments of the zone that day (teams expanded to their In Use members) for people without a record yet.
/// </summary>
public sealed class AttendanceSheetQuery(
    IAttendanceRepository attendance,
    IWorkAssignmentRepository assignments,
    IWorkforceRepository workforce,
    IPayrollRepository payrolls,
    LaborEvidence evidence)
{
    public async Task<Result<AttendanceSheet>> GetAsync(Guid zoneId, DateOnly? date, CancellationToken cancellationToken)
    {
        if (await assignments.GetZoneAsync(zoneId, cancellationToken) is null)
        {
            return AppError.NotFound;
        }

        if (date is not { } day)
        {
            return AppError.Invalid("Date");
        }

        var rows = new List<AttendanceSheetRow>();
        foreach (var record in (await attendance.ListForZoneAsync(zoneId, day, cancellationToken)).OrderBy(r => r.Employee?.FullName ?? r.Worker?.FullName))
        {
            rows.Add(await RowAsync(record, cancellationToken));
        }

        var suggestions = await SuggestionsAsync(zoneId, day, cancellationToken);
        var recorded = await attendance.ListForPeopleAsync(
            day, suggestions.Where(s => s.Kind == "Employee").Select(s => s.PersonId).ToList(),
            suggestions.Where(s => s.Kind == "Worker").Select(s => s.PersonId).ToList(), cancellationToken);
        rows.AddRange(suggestions.Where(s => recorded.All(r => (r.EmployeeId ?? r.WorkerId) != s.PersonId)));

        var locked = (await payrolls.GetFinalizedPeriodsAsync([PayrollPeriod.Of(day).Key], cancellationToken)).Count > 0;
        return new AttendanceSheet(zoneId, day, locked, rows);
    }

    private async Task<List<AttendanceSheetRow>> SuggestionsAsync(Guid zoneId, DateOnly day, CancellationToken cancellationToken)
    {
        var work = await assignments.ListActiveDailyWageAsync(zoneId, day, cancellationToken);
        var teamIds = work.Where(a => a.ContractorId is not null).Select(a => a.ContractorId!.Value).Distinct().ToList();
        var members = (await workforce.GetInUseMembersAsync(teamIds, cancellationToken)).ToLookup(m => m.ContractorId!.Value);
        var suggestions = new Dictionary<Guid, AttendanceSheetRow>();
        foreach (var assignment in work)
        {
            if (assignment.Employee is { } e)
            {
                suggestions.TryAdd(e.Id, Suggested("Employee", e.Id, e.FullName, null, assignment.Id));
            }
            else if (assignment.Worker is { } w)
            {
                var team = w.Contractor is { } own ? new TeamRef(own.Id, own.Name) : null;
                suggestions.TryAdd(w.Id, Suggested("Worker", w.Id, w.FullName, team, assignment.Id));
            }
            else if (assignment.Contractor is { } t)
            {
                foreach (var member in members[t.Id])
                {
                    suggestions.TryAdd(member.Id, Suggested("Worker", member.Id, member.FullName, new TeamRef(t.Id, t.Name), assignment.Id));
                }
            }
        }

        return suggestions.Values.OrderBy(s => s.Name).ToList();
    }

    public async Task<AttendanceSheetRow> RowAsync(AttendanceRecord r, CancellationToken cancellationToken) => new(
        r.Id, false, r.EmployeeId is not null ? "Employee" : "Worker", (r.EmployeeId ?? r.WorkerId)!.Value,
        r.Employee?.FullName ?? r.Worker?.FullName ?? string.Empty,
        r.Contractor is { } team ? new TeamRef(team.Id, team.Name) : null,
        r.Result, r.Hours, r.Notes, await evidence.ReadUrlsAsync(r.EvidenceUrls, cancellationToken), null, RowVersionGuard.Encode(r.RowVersion));

    private static AttendanceSheetRow Suggested(string kind, Guid id, string name, TeamRef? team, Guid assignmentId) =>
        new(null, true, kind, id, name, team, null, null, null, [], assignmentId, null);
}
