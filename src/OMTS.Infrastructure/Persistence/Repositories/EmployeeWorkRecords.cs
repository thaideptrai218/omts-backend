using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Infrastructure.Persistence.Repositories;

/// <summary>
/// What ending an employment needs from attendance (LABOR-04) and work assignments (LABOR-02): the end action and the daily job
/// both call <see cref="RemoveAssignmentsAfterAsync"/>, so a person ended today loses the assignments of later days on either path.
/// </summary>
internal sealed class EmployeeWorkRecords(OmtsDbContext db) : IEmployeeWorkRecords
{
    public async Task<DateOnly?> LastAttendanceDayAsync(Guid employeeId, CancellationToken cancellationToken) =>
        await db.Attendances.AsNoTracking().Where(a => a.EmployeeId == employeeId).MaxAsync(a => (DateOnly?)a.Date, cancellationToken);

    /// <summary>Tracked removal, saved with the caller's change; their attendance and pay records stay (BR-LABOR-01).</summary>
    public async Task RemoveAssignmentsAfterAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken) =>
        db.WorkAssignments.RemoveRange(await db.WorkAssignments.Where(w => w.EmployeeId == employeeId && w.Date > day).ToListAsync(cancellationToken));
}
