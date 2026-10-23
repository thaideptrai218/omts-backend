using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class AttendanceRepository(OmtsDbContext db) : IAttendanceRepository
{
    public async Task<IReadOnlyList<AttendanceRecord>> ListForZoneAsync(Guid zoneId, DateOnly date, CancellationToken cancellationToken) =>
        await db.AttendanceRecords
            .Include(r => r.Zone).Include(r => r.Employee).Include(r => r.Worker).Include(r => r.Contractor)
            .Where(r => r.ZoneId == zoneId && r.WorkDate == date)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AttendanceRecord>> ListForPeopleAsync(
        DateOnly date, IReadOnlyCollection<Guid> employeeIds, IReadOnlyCollection<Guid> workerIds, CancellationToken cancellationToken) =>
        await db.AttendanceRecords.Include(r => r.Zone)
            .Where(r => r.WorkDate == date
                && ((r.EmployeeId != null && employeeIds.Contains(r.EmployeeId.Value)) || (r.WorkerId != null && workerIds.Contains(r.WorkerId.Value))))
            .ToListAsync(cancellationToken);

    public Task<AttendanceRecord?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.AttendanceRecords.Include(r => r.Zone).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<AttendanceRecord>> ListInRangeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        await db.AttendanceRecords.AsNoTracking().Where(r => r.WorkDate >= from && r.WorkDate <= to).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AttendanceRecord>> ListForEmployeeAsync(
        Guid employeeId, IReadOnlyCollection<Guid> workerIds, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        await db.AttendanceRecords.AsNoTracking().Include(r => r.Zone)
            .Where(r => r.WorkDate >= from && r.WorkDate <= to
                && (r.EmployeeId == employeeId || (r.WorkerId != null && workerIds.Contains(r.WorkerId.Value))))
            .OrderBy(r => r.WorkDate)
            .ToListAsync(cancellationToken);

    public void Add(AttendanceRecord record) => db.AttendanceRecords.Add(record);
}
