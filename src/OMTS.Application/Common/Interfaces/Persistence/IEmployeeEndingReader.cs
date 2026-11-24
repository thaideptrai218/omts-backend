namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>A work assignment of a day, as the summary of an ending employment or of removing a zone lists it.</summary>
/// <param name="Place">The raft code, or "{zone code} (zone)" for work with no raft.</param>
public sealed record AssignmentOnDay(DateOnly Date, string Place, string JobName);

/// <summary>
/// Read-only questions about an employee's work that the consequences of ending an employment (L9) and of taking a working zone off a profile (L16)
/// ask: they read nothing a use case would change, so they stay apart from <see cref="IEmployeeWorkRecords"/>.
/// </summary>
public interface IEmployeeEndingReader
{
    /// <summary>The work assignments of the employee after <paramref name="day"/>, oldest first: what ending the employment on that day removes.</summary>
    Task<IReadOnlyList<AssignmentOnDay>> ListAssignmentsAfterAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The assignments of the employee from <paramref name="from"/> on in the zone, oldest first.</summary>
    Task<IReadOnlyList<AssignmentOnDay>> ListAssignmentsInZoneFromAsync(Guid employeeId, Guid zoneId, DateOnly from, CancellationToken cancellationToken);

    /// <summary>The days the employee checked in and has not checked out.</summary>
    Task<IReadOnlyList<DateOnly>> ListOpenCheckInDaysAsync(Guid employeeId, CancellationToken cancellationToken);
}
