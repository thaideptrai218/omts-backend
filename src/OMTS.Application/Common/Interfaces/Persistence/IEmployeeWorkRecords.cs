namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// What ending an employment needs from attendance (LABOR-04) and work assignments (LABOR-02), behind one port so those PRs
/// fill it without touching <c>EmploymentService</c>.
/// </summary>
public interface IEmployeeWorkRecords
{
    /// <summary>The latest day with attendance of the employee, if any: the last working day may not be before it (LABOR-01.3.E1).</summary>
    Task<DateOnly?> LastAttendanceDayAsync(Guid employeeId, CancellationToken cancellationToken);

    /// <summary>
    /// Adds to the unit of work the removal of the work assignments of the employee after <paramref name="day"/>, without
    /// saving (LABOR-01.3 step 4); their attendance and pay records stay.
    /// </summary>
    Task RemoveAssignmentsAfterAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken);
}
