using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Sales;
using OMTS.Infrastructure.Persistence.Repositories.Sales;

namespace OMTS.Infrastructure.Persistence.Repositories;

/// <summary>
/// Employment ending preserves recorded attendance and cancels future assignments and shift reservations.
/// </summary>
internal sealed class EmployeeWorkRecords(OmtsDbContext db, TimeProvider clock, ICurrentUser user) : IEmployeeWorkRecords
{
    public Task<DateOnly?> LastAttendanceDayAsync(Guid employeeId, CancellationToken cancellationToken) =>
        db.AttendanceRecords.Where(a => a.EmployeeId == employeeId).Select(a => (DateOnly?)a.WorkDate).MaxAsync(cancellationToken);

    public async Task RemoveAssignmentsAfterAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken)
    {
        foreach (var assignment in await db.WorkAssignments.Where(a => a.EmployeeId == employeeId && a.WorkDate > day && a.CancelledAt == null).ToListAsync(cancellationToken))
        {
            assignment.CancelledAt = clock.GetUtcNow();
            assignment.CancelledByAccountId = user.AccountId;
            assignment.CancellationReason = "Employment ended";
        }
        foreach (var invite in await db.Set<PurchaseShiftInvitation>().Where(i => i.EmployeeId == employeeId && i.ReservedWorkDate > day).ToListAsync(cancellationToken))
            invite.ReservedWorkDate = null;
    }
}
