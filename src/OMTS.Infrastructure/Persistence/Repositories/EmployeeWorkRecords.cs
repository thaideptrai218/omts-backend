using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Infrastructure.Persistence.Repositories;

/// <summary>
/// What ending an employment needs from work assignments (LABOR-02, done) and attendance (LABOR-04, L5). There is no Attendance
/// table yet, so <see cref="LastAttendanceDayAsync"/> answers no attendance until L5 reads it. The assignments after the last
/// working day are removed by the end action and again by the daily job, which both call <see cref="RemoveAssignmentsAfterAsync"/>.
/// </summary>
internal sealed class EmployeeWorkRecords(OmtsDbContext db) : IEmployeeWorkRecords
{
    public Task<DateOnly?> LastAttendanceDayAsync(Guid employeeId, CancellationToken cancellationToken) =>
        Task.FromResult<DateOnly?>(null);

    // Tracked, so the caller's own save deletes them together with the end of the employment; the job periods of a later day go too.
    public async Task RemoveAssignmentsAfterAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken) =>
        db.WorkAssignments.RemoveRange(await db.WorkAssignments.Where(a => a.EmployeeId == employeeId && a.WorkDate > day).ToListAsync(cancellationToken));

    // Tracked rows, not a set-based delete: the audit and row-version interceptors see them, and the in-memory provider used by the tests has no bulk delete.
    public async Task<int> RemoveAssignmentsAfterLastWorkingDaysAsync(CancellationToken cancellationToken)
    {
        var stray = await db.WorkAssignments
            .Where(a => a.Employee.LastWorkingDay != null && a.WorkDate > a.Employee.LastWorkingDay)
            .ToListAsync(cancellationToken);
        db.WorkAssignments.RemoveRange(stray);
        return stray.Count;
    }
}
