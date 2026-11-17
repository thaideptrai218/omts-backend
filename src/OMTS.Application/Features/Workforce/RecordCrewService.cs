using OMTS.Application.Common.Results;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Workforce;

/// <summary>An employee on a nursery, seeding or harvest record.</summary>
/// <param name="Code">The employee code (NV000001); null for a row older than the column.</param>
public sealed record CrewMemberView(Guid Id, string? Code, string Name);

/// <summary>What <see cref="RecordCrewService.Apply{TRow}"/> did to the rows of a record: the employees newly named (the ones the work assignment is made for) and whether anything changed.</summary>
public sealed record CrewChange(IReadOnlyList<Employee> Added, bool Changed);

/// <summary>
/// The crew of a record (harvest lot since 12/11/2026, nursery batch and seeding session since 17/11/2026): an optional list of employees, for the record only. Each must exist, work in the cooperative and
/// still be employed. One employee may be on the records of several rafts on the same day, and no quota or pay is counted per person. Sent as one <c>crew</c> form field per employee id; the rules are the
/// same for the three records, so they are written once here.
/// </summary>
public sealed class RecordCrewService(IEmployeeRepository employees, ICooperativeRepository cooperatives, IRecordEmployeeStore store)
{
    public const string CrewField = "Crew";

    /// <summary>A trip rarely has more hands than this: it keeps a typing error from filling a record with the whole staff.</summary>
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
            return (null, AppError.Invalid(CrewField, $"A record lists at most {MaxCrew} employees."));
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

    /// <summary>
    /// Makes the crew of a record the given employees: rows no longer wanted go, new ones come, the others stay. <paramref name="links"/> is the crew collection of the record and
    /// <paramref name="create"/> makes the row of one employee for it.
    /// </summary>
    public CrewChange Apply<TRow>(ICollection<TRow> links, IReadOnlyList<Employee> wanted, Func<Employee, TRow> create) where TRow : class, IRecordEmployee
    {
        var wantedIds = wanted.Select(e => e.Id).ToHashSet();
        var changed = false;
        foreach (var row in links.Where(m => !wantedIds.Contains(m.EmployeeId)).ToList())
        {
            links.Remove(row);
            store.Remove(row);
            changed = true;
        }

        var added = wanted.Where(e => links.All(m => m.EmployeeId != e.Id)).ToList();
        foreach (var employee in added)
        {
            var row = create(employee);
            links.Add(row);
            store.Add(row);
            changed = true;
        }

        return new CrewChange(added, changed);
    }

    public static IReadOnlyList<CrewMemberView> ViewOf(IEnumerable<IRecordEmployee> crew) => crew
        .Where(m => m.Employee is not null)
        .Select(m => new CrewMemberView(m.EmployeeId, m.Employee.Code, m.Employee.FullName))
        .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.Code, StringComparer.Ordinal)
        .ToList();

    /// <summary>The names of the crew for the audit log, in name order.</summary>
    public static IReadOnlyList<string> AuditNames(IEnumerable<IRecordEmployee> crew) => crew
        .Where(m => m.Employee is not null)
        .Select(m => m.Employee.Code is { Length: > 0 } code ? $"{code} {m.Employee.FullName}" : m.Employee.FullName)
        .Order(StringComparer.Ordinal)
        .ToList();
}
