using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class EmployeeRepository(OmtsDbContext db) : IEmployeeRepository
{
    public Task<Employee?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Employees.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public Task<Employee?> GetForEditAsync(Guid id, CancellationToken cancellationToken) =>
        db.Employees
            .Include(e => e.WorkingZones).ThenInclude(w => w.Zone)
            .Include(e => e.JobTypes).ThenInclude(j => j.JobType)
            .Include(e => e.TypePeriods)
            .Include(e => e.Account)
            .AsSplitQuery()
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public Task<Employee?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken) =>
        db.Employees.FirstOrDefaultAsync(e => e.AccountId == accountId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, Guid?>> GetIdsByAccountIdsAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken) =>
        await db.Employees.AsNoTracking()
            .Where(e => e.AccountId != null && accountIds.Contains(e.AccountId.Value))
            .ToDictionaryAsync(e => e.AccountId!.Value, e => (Guid?)e.Id, cancellationToken);

    public async Task<(IReadOnlyList<Employee> Items, int Total)> SearchAsync(
        string? search, string? phoneNumber, int? statusId, Guid? zoneId, IReadOnlyCollection<Guid>? firstZoneIds, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Employees.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            query = phoneNumber is null
                ? query.Where(e => e.FullName.Contains(text) || (e.PhoneNumber != null && e.PhoneNumber.Contains(text)))
                : query.Where(e => e.FullName.Contains(text) || (e.PhoneNumber != null && (e.PhoneNumber.Contains(text) || e.PhoneNumber == phoneNumber)));
        }

        if (statusId is not null)
        {
            query = query.Where(e => e.StatusId == statusId);
        }

        if (zoneId is not null)
        {
            query = query.Where(e => e.WorkingZones.Any(w => w.ZoneId == zoneId));
        }

        var total = await query.CountAsync(cancellationToken);
        var ordered = firstZoneIds is null
            ? query.OrderBy(e => e.FullName)
            : query.OrderByDescending(e => e.WorkingZones.Any(w => firstZoneIds.Contains(w.ZoneId))).ThenBy(e => e.FullName);
        var items = await ordered.ThenBy(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Include(e => e.WorkingZones).ThenInclude(w => w.Zone)
            .Include(e => e.JobTypes).ThenInclude(j => j.JobType)
            .Include(e => e.TypePeriods)
            .Include(e => e.Account)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<Employee?> FindEmployedWithPhoneAsync(string phoneNumber, Guid? exceptEmployeeId, CancellationToken cancellationToken) =>
        db.Employees.AsNoTracking().FirstOrDefaultAsync(
            e => e.PhoneNumber == phoneNumber && e.StatusId == StatusIds.Employee.Working && e.Id != exceptEmployeeId, cancellationToken);

    public async Task<IReadOnlyList<JobType>> GetJobTypesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0 ? [] : await db.JobTypes.AsNoTracking().Where(j => ids.Contains(j.Id)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListEmployedWithLastWorkingDayBeforeAsync(DateOnly day, CancellationToken cancellationToken) =>
        await db.Employees.AsNoTracking()
            .Where(e => e.StatusId == StatusIds.Employee.Working && e.LastWorkingDay < day)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

    public void Add(Employee employee) => db.Employees.Add(employee);

    public void AddWorkingZone(EmployeeWorkingZone workingZone) => db.EmployeeWorkingZones.Add(workingZone);

    public void RemoveWorkingZone(EmployeeWorkingZone workingZone) => db.EmployeeWorkingZones.Remove(workingZone);

    public void AddJobType(EmployeeJobType jobType) => db.EmployeeJobTypes.Add(jobType);

    public void RemoveJobType(EmployeeJobType jobType) => db.EmployeeJobTypes.Remove(jobType);

    public void AddTypePeriod(EmployeeTypePeriod period) => db.EmployeeTypePeriods.Add(period);
}
