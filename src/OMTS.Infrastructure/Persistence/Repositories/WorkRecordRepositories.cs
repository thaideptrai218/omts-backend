using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class WorkAssignmentRepository(OmtsDbContext db) : IWorkAssignmentRepository
{
    public Task<WorkAssignment?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.WorkAssignments.Include(w => w.Raft).ThenInclude(r => r.Zone).Include(w => w.JobType).Include(w => w.Employee)
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

    public async Task<IReadOnlyList<WorkAssignment>> ListDayAsync(DateOnly day, CancellationToken cancellationToken) =>
        await db.WorkAssignments.AsNoTracking().Include(w => w.Raft).ThenInclude(r => r.Zone).Include(w => w.JobType).Include(w => w.Employee)
            .Where(w => w.Date == day)
            .OrderBy(w => w.Employee.FullName).ThenBy(w => w.EmployeeId).ThenBy(w => w.StartsAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WorkAssignment>> ListOfEmployeesAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly day, CancellationToken cancellationToken) =>
        employeeIds.Count == 0
            ? []
            : await db.WorkAssignments.Include(w => w.Raft).ThenInclude(r => r.Zone).Include(w => w.JobType).Include(w => w.Employee)
                .Where(w => w.Date == day && employeeIds.Contains(w.EmployeeId))
                .OrderBy(w => w.StartsAt)
                .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WorkAssignment>> ListOfEmployeeAsync(Guid employeeId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        await db.WorkAssignments.AsNoTracking().Include(w => w.Raft).ThenInclude(r => r.Zone).Include(w => w.JobType).Include(w => w.Employee)
            .Where(w => w.EmployeeId == employeeId && w.Date >= from && w.Date <= to)
            .OrderBy(w => w.Date).ThenBy(w => w.StartsAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<JobType>> GetJobTypesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0 ? [] : await db.JobTypes.AsNoTracking().Where(j => ids.Contains(j.Id)).ToListAsync(cancellationToken);

    public Task<WorkAssignment?> FindFirstAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken) =>
        db.WorkAssignments.Include(w => w.Raft).ThenInclude(r => r.Zone).Include(w => w.JobType).Include(w => w.Employee)
            .FirstOrDefaultAsync(w => w.EmployeeId == employeeId && w.Date == day && w.StartsAt == null, cancellationToken);

    public void Add(WorkAssignment assignment) => db.WorkAssignments.Add(assignment);

    public void Remove(WorkAssignment assignment) => db.WorkAssignments.Remove(assignment);
}

internal sealed class AttendanceRepository(OmtsDbContext db) : IAttendanceRepository
{
    public Task<Attendance?> GetAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken) =>
        db.Attendances.FirstOrDefaultAsync(a => a.EmployeeId == employeeId && a.Date == day, cancellationToken);

    public async Task<IReadOnlyList<Attendance>> ListOfEmployeesAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly day, CancellationToken cancellationToken) =>
        employeeIds.Count == 0 ? [] : await db.Attendances.Where(a => a.Date == day && employeeIds.Contains(a.EmployeeId)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Attendance>> ListDayAsync(DateOnly day, CancellationToken cancellationToken) =>
        await db.Attendances.AsNoTracking().Where(a => a.Date == day).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Attendance>> ListOfEmployeeAsync(Guid employeeId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        await db.Attendances.AsNoTracking().Where(a => a.EmployeeId == employeeId && a.Date >= from && a.Date <= to).OrderBy(a => a.Date).ToListAsync(cancellationToken);

    public void Add(Attendance attendance) => db.Attendances.Add(attendance);
}

internal sealed class EmployeeRequestRepository(OmtsDbContext db) : IEmployeeRequestRepository
{
    public Task<EmployeeRequest?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.EmployeeRequests.Include(r => r.Employee).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<EmployeeRequest?> FindByClientOperationAsync(Guid employeeId, Guid clientOperationId, CancellationToken cancellationToken) =>
        db.EmployeeRequests.Include(r => r.Employee).FirstOrDefaultAsync(r => r.EmployeeId == employeeId && r.ClientOperationId == clientOperationId, cancellationToken);

    public Task<EmployeeRequest?> FindAwaitingAsync(Guid employeeId, EmployeeRequestType type, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        db.EmployeeRequests.AsNoTracking().FirstOrDefaultAsync(
            r => r.EmployeeId == employeeId && r.Type == type && r.StatusId == StatusIds.EmployeeRequest.AwaitingReview && r.FromDate <= to && r.ToDate >= from,
            cancellationToken);

    public async Task<IReadOnlyList<EmployeeRequest>> ListAsync(int? statusId, int take, CancellationToken cancellationToken)
    {
        var query = db.EmployeeRequests.AsNoTracking().Include(r => r.Employee).Where(r => statusId == null || r.StatusId == statusId);
        // Awaiting review first (the lowest status id), the oldest first; the others newest first (SCR-LABOR-03).
        var awaiting = StatusIds.EmployeeRequest.AwaitingReview;
        return await query
            .OrderByDescending(r => r.StatusId == awaiting)
            .ThenBy(r => r.StatusId == awaiting ? r.SentAt : DateTimeOffset.MaxValue)
            .ThenByDescending(r => r.SentAt)
            .ThenBy(r => r.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EmployeeRequest>> ListOfEmployeeAsync(Guid employeeId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        await db.EmployeeRequests.AsNoTracking().Include(r => r.Employee)
            .Where(r => r.EmployeeId == employeeId
                && (r.StatusId == StatusIds.EmployeeRequest.AwaitingReview || r.FromDate <= to && r.ToDate >= from))
            .OrderByDescending(r => r.SentAt).ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<EmployeeRequest>> ListApprovedLeaveAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly day, CancellationToken cancellationToken) =>
        employeeIds.Count == 0
            ? []
            : await db.EmployeeRequests.AsNoTracking()
                .Where(r => employeeIds.Contains(r.EmployeeId) && r.Type == EmployeeRequestType.Leave
                    && r.StatusId == StatusIds.EmployeeRequest.Approved && r.FromDate <= day && r.ToDate >= day)
                .ToListAsync(cancellationToken);

    public void Add(EmployeeRequest request) => db.EmployeeRequests.Add(request);
}

internal sealed class WorkEmployeeReader(OmtsDbContext db) : IWorkEmployeeReader
{
    public async Task<IReadOnlyList<Employee>> GetForWorkAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? []
            : await db.Employees.Include(e => e.WorkingZones).Include(e => e.TypePeriods).Include(e => e.JobTypes)
                .Where(e => ids.Contains(e.Id)).AsSplitQuery().ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Employee>> ListEmployedAsync(IReadOnlyCollection<Guid>? zoneIds, DateOnly day, CancellationToken cancellationToken)
    {
        var query = db.Employees.AsNoTracking()
            .Where(e => e.StatusId == StatusIds.Employee.Working && (e.LastWorkingDay == null || e.LastWorkingDay >= day));
        if (zoneIds is not null)
        {
            query = query.Where(e => e.WorkingZones.Any(w => zoneIds.Contains(w.ZoneId)));
        }

        return await query.Include(e => e.WorkingZones).Include(e => e.TypePeriods).Include(e => e.JobTypes)
            .OrderBy(e => e.FullName).ThenBy(e => e.Id).AsSplitQuery().ToListAsync(cancellationToken);
    }
}
