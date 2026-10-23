using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Workforce;
using OMTS.Application.Features.WorkTracking.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WorkTracking;

/// <summary>One person of the sheet after team expansion; <see cref="Index"/> is the request row it came from.</summary>
public sealed record AttendanceEntry(
    int Index, Employee? Employee, SeasonalWorker? Worker, Guid? TeamOfDayId, AttendanceResult Result, decimal? Hours, string? Notes, string? Reason)
{
    public string Name => Employee?.FullName ?? Worker!.FullName;
}

/// <summary>
/// LAB-04 sheet rows → one entry per person: a team row expands to every In Use member (team of the day = that team)
/// and an individual row of the same worker overrides it. Every row is checked; errors are reported per row.
/// </summary>
public sealed class AttendanceRowExpander(IWorkforceRepository workforce)
{
    public async Task<(List<AttendanceEntry> Entries, List<AttendanceRowError> Errors)> ExpandAsync(
        IReadOnlyList<SaveAttendanceRow> rows, DateOnly date, CancellationToken cancellationToken)
    {
        var employees = (await workforce.GetEmployeesAsync(Ids(rows, r => r.EmployeeId), cancellationToken)).ToDictionary(e => e.Id);
        var workers = (await workforce.GetWorkersAsync(Ids(rows, r => r.WorkerId), cancellationToken)).ToDictionary(w => w.Id);
        var teamIds = Ids(rows, r => r.TeamId).Concat(Ids(rows, r => r.TeamOfDayId)).Distinct().ToList();
        var teams = (await workforce.GetTeamsAsync(teamIds, cancellationToken)).ToDictionary(t => t.Id);
        var members = (await workforce.GetInUseMembersAsync(Ids(rows, r => r.TeamId), cancellationToken)).ToLookup(m => m.ContractorId!.Value);

        var errors = new List<AttendanceRowError>();
        var fromTeams = new Dictionary<Guid, AttendanceEntry>();
        var individual = new List<AttendanceEntry>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var invalid = new List<string>();
            if (new[] { row.EmployeeId, row.WorkerId, row.TeamId }.Count(id => id is not null) != 1)
            {
                invalid.Add("Person");
            }

            if (row.Result is null)
            {
                invalid.Add("Result");
            }

            if (row.Hours is { } hours && (hours < 0.5m || hours > 24 || hours * 2 != decimal.Truncate(hours * 2)))
            {
                invalid.Add("Hours");
            }

            var notes = LaborInputRules.Optional(row.Notes, LaborInputRules.NotesLength, "Notes", invalid);
            var reason = LaborInputRules.Optional(row.Reason, LaborInputRules.ReasonLength, "Reason", invalid);
            if (invalid.Count > 0)
            {
                errors.Add(Error(index, null, AppError.InvalidFields(invalid)));
                continue;
            }

            if (row.TeamId is { } teamId)
            {
                if (!teams.TryGetValue(teamId, out var team) || team.StatusId != StatusIds.Usage.InUse)
                {
                    errors.Add(Error(index, team?.Name, AppError.Invalid("Team")));
                    continue;
                }

                foreach (var member in members[teamId])
                {
                    fromTeams[member.Id] = new AttendanceEntry(index, null, member, teamId, row.Result!.Value, row.Hours, notes, reason);
                }

                continue;
            }

            if (row.EmployeeId is { } employeeId)
            {
                var problem = !employees.TryGetValue(employeeId, out var employee) ? AppError.Invalid("Employee")
                    : row.TeamOfDayId is not null ? AppError.Invalid("Team of the day")
                    : LaborPersonRules.LeftOn(employee, date);
                if (problem is not null)
                {
                    errors.Add(Error(index, employee?.FullName, problem));
                    continue;
                }

                individual.Add(new AttendanceEntry(index, employee, null, null, row.Result!.Value, row.Hours, notes, reason));
                continue;
            }

            var found = workers.GetValueOrDefault(row.WorkerId!.Value);
            var teamOfDay = row.TeamOfDayId ?? found?.ContractorId;
            var workerProblem = found is null ? AppError.Invalid("Worker")
                : row.TeamOfDayId is { } dayTeam && !teams.ContainsKey(dayTeam) ? AppError.Invalid("Team of the day")
                : null;
            if (workerProblem is not null)
            {
                errors.Add(Error(index, found?.FullName, workerProblem));
                continue;
            }

            individual.Add(new AttendanceEntry(index, null, found, teamOfDay, row.Result!.Value, row.Hours, notes, reason));
        }

        var entries = new List<AttendanceEntry>();
        foreach (var entry in individual)
        {
            var duplicate = entries.Any(e => (e.Employee is not null && e.Employee.Id == entry.Employee?.Id) || (e.Worker is not null && e.Worker.Id == entry.Worker?.Id));
            if (duplicate)
            {
                errors.Add(Error(entry.Index, entry.Name, AppError.Invalid("Person", "The same person appears twice on the sheet.")));
                continue;
            }

            entries.Add(entry);
        }

        // Individual rows override the team rows of the same worker.
        entries.AddRange(fromTeams.Values.Where(t => entries.All(e => e.Worker?.Id != t.Worker!.Id)));
        return (entries, errors);
    }

    public static AttendanceRowError Error(int index, string? person, AppError error) => new(
        index, person, error.Message.Code, error.Message.Text,
        error.Details is not null && error.Details.TryGetValue("field", out var field) ? field as string : null);

    private static List<Guid> Ids(IEnumerable<SaveAttendanceRow> rows, Func<SaveAttendanceRow, Guid?> pick) =>
        rows.Select(pick).Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
}
