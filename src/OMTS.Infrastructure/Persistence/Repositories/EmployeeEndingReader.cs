using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Infrastructure.Persistence.Repositories;

/// <summary>Read-only answers about an employee's assignments and open check-ins for the summary of ending an employment and for removing a working zone.</summary>
internal sealed class EmployeeEndingReader(OmtsDbContext db) : IEmployeeEndingReader
{
    public async Task<IReadOnlyList<AssignmentOnDay>> ListAssignmentsAfterAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken) =>
        await Query(db.WorkAssignments.AsNoTracking().Where(w => w.EmployeeId == employeeId && w.Date > day), cancellationToken);

    public async Task<IReadOnlyList<AssignmentOnDay>> ListAssignmentsInZoneFromAsync(Guid employeeId, Guid zoneId, DateOnly from, CancellationToken cancellationToken) =>
        await Query(db.WorkAssignments.AsNoTracking().Where(w => w.EmployeeId == employeeId && w.ZoneId == zoneId && w.Date >= from), cancellationToken);

    public async Task<IReadOnlyList<DateOnly>> ListOpenCheckInDaysAsync(Guid employeeId, CancellationToken cancellationToken) =>
        await db.Attendances.AsNoTracking()
            .Where(a => a.EmployeeId == employeeId && a.CheckInAt != null && a.CheckOutAt == null)
            .OrderBy(a => a.Date).Select(a => a.Date).ToListAsync(cancellationToken);

    private static async Task<IReadOnlyList<AssignmentOnDay>> Query(IQueryable<OMTS.Domain.Entities.Labor.WorkAssignment> assignments, CancellationToken cancellationToken) =>
        (await assignments.OrderBy(w => w.Date).ThenBy(w => w.StartsAt)
            .Select(w => new { w.Date, Raft = w.Raft != null ? w.Raft.Code : null, Zone = w.Zone.Code, Job = w.JobType.Name })
            .ToListAsync(cancellationToken))
        .Select(w => new AssignmentOnDay(w.Date, w.Raft ?? $"{w.Zone} (zone)", w.Job))
        .ToList();
}
