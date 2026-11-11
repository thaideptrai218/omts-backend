using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class WorkAssignmentRepository(OmtsDbContext db) : IWorkAssignmentRepository
{
    public Task<Raft?> GetRaftAsync(Guid raftId, CancellationToken cancellationToken) =>
        db.Rafts.Include(r => r.Zone).FirstOrDefaultAsync(r => r.Id == raftId, cancellationToken);

    public Task<JobType?> GetJobTypeAsync(Guid jobTypeId, CancellationToken cancellationToken) =>
        db.JobTypes.FirstOrDefaultAsync(j => j.Id == jobTypeId, cancellationToken);

    public Task<WorkAssignment?> GetForEditAsync(Guid id, CancellationToken cancellationToken) =>
        db.WorkAssignments
            .Include(a => a.Employee).ThenInclude(e => e.WorkingZones)
            .Include(a => a.Raft).ThenInclude(r => r.Zone)
            .Include(a => a.JobType)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task<IReadOnlyList<WorkAssignment>> ListOfEmployeesOnAsync(
        IReadOnlyCollection<Guid> employeeIds, DateOnly date, bool tracked, CancellationToken cancellationToken)
    {
        var query = db.WorkAssignments.Where(a => a.WorkDate == date && employeeIds.Contains(a.EmployeeId));
        query = tracked ? query : query.AsNoTracking();
        return await query
            .Include(a => a.Employee)
            .Include(a => a.Raft).ThenInclude(r => r.Zone)
            .Include(a => a.JobType)
            .OrderBy(a => a.StartTime).ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkAssignment>> ListOnAsync(DateOnly date, PermittedZones zones, CancellationToken cancellationToken)
    {
        var query = db.WorkAssignments.AsNoTracking().Where(a => a.WorkDate == date);
        if (!zones.AllZones)
        {
            // Every period of an employee who has one in the zones: the periods on other rafts decide where a period ends.
            var zoneIds = zones.ZoneIds.ToList();
            var employeeIds = db.WorkAssignments.Where(a => a.WorkDate == date && zoneIds.Contains(a.Raft.ZoneId)).Select(a => a.EmployeeId);
            query = query.Where(a => employeeIds.Contains(a.EmployeeId));
        }

        return await query
            .Include(a => a.Employee)
            .Include(a => a.Raft).ThenInclude(r => r.Zone)
            .Include(a => a.JobType)
            .OrderBy(a => a.StartTime).ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Raft>> ListRaftsInUseAsync(PermittedZones zones, CancellationToken cancellationToken)
    {
        var query = db.Rafts.AsNoTracking().Where(r => r.StatusId == StatusIds.Usage.InUse);
        if (!zones.AllZones)
        {
            var zoneIds = zones.ZoneIds.ToList();
            query = query.Where(r => zoneIds.Contains(r.ZoneId));
        }

        return await query.Include(r => r.Zone).OrderBy(r => r.Zone.Code).ThenBy(r => r.Code).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Employee>> GetEmployeesAsync(IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken) =>
        await db.Employees.Include(e => e.WorkingZones).Where(e => employeeIds.Contains(e.Id)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Employee>> ListEmployedAsync(
        PermittedZones? zones, string? search, string? phoneNumber, Guid? jobTypeFirst, int limit, CancellationToken cancellationToken)
    {
        var query = db.Employees.AsNoTracking().Where(e => e.StatusId == StatusIds.Employee.Working);
        if (zones is { AllZones: false })
        {
            var zoneIds = zones.ZoneIds.ToList();
            query = query.Where(e => e.WorkingZones.Any(w => zoneIds.Contains(w.ZoneId)));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = phoneNumber is null
                ? query.Where(e => e.FullName.Contains(search) || (e.PhoneNumber != null && e.PhoneNumber.Contains(search)))
                : query.Where(e => e.FullName.Contains(search) || (e.PhoneNumber != null && (e.PhoneNumber.Contains(search) || e.PhoneNumber == phoneNumber)));
        }

        var ordered = jobTypeFirst is { } jobType
            ? query.OrderByDescending(e => e.JobTypes.Any(j => j.JobTypeId == jobType)).ThenBy(e => e.FullName)
            : query.OrderBy(e => e.FullName);
        return await ordered.ThenBy(e => e.Id).Take(limit)
            .Include(e => e.WorkingZones)
            .Include(e => e.JobTypes)
            .Include(e => e.TypePeriods)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
    }

    public void Add(WorkAssignment assignment) => db.WorkAssignments.Add(assignment);

    public void Remove(WorkAssignment assignment) => db.WorkAssignments.Remove(assignment);
}

/// <summary>Until LABOR-04 (L5) creates the Attendance table nobody is checked in; L5 replaces this reader with one that reads it.</summary>
internal sealed class EmptyAttendanceDayReader : IAttendanceDayReader
{
    public Task<IReadOnlyDictionary<Guid, DayAttendance>> ReadAsync(DateOnly date, IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, DayAttendance>>(new Dictionary<Guid, DayAttendance>());
}
