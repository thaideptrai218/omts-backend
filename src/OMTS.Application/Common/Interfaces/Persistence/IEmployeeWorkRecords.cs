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
    /// saving (LABOR-01.3 step 4); their attendance and pay records stay. Called at the end action and again by the daily
    /// job (<c>EmploymentEndService</c>), as an employee ended today can still be assigned until then: L4 must cover both.
    /// </summary>
    Task RemoveAssignmentsAfterAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken);

    /// <summary>
    /// Adds to the unit of work the removal of every work assignment dated after the last working day of its employee, for each
    /// employee who has one, whatever the status (an assign that raced an end in the past leaves such a row on a Left employee).
    /// Returns how many; nothing to remove changes nothing, so the daily job can run it again and again.
    /// </summary>
    Task<int> RemoveAssignmentsAfterLastWorkingDaysAsync(CancellationToken cancellationToken);
}
