using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

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
        EmployeeSearchFilter filter, IReadOnlyCollection<Guid>? firstZoneIds, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = Filtered(filter);
        var total = await query.CountAsync(cancellationToken);
        var items = await WithRows(Ordered(query, filter, firstZoneIds).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<Employee>> ListAsync(
        EmployeeSearchFilter filter, IReadOnlyCollection<Guid>? firstZoneIds, int limit, CancellationToken cancellationToken) =>
        await WithRows(Ordered(Filtered(filter), filter, firstZoneIds).Take(limit + 1)).ToListAsync(cancellationToken);

    public Task<Employee?> FindByCodeAsync(string code, Guid? exceptEmployeeId, CancellationToken cancellationToken) =>
        db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Code != null && e.Code.ToUpper() == code.ToUpper() && e.Id != exceptEmployeeId, cancellationToken);

    private IQueryable<Employee> Filtered(EmployeeSearchFilter filter)
    {
        var query = db.Employees.AsNoTracking();
        if (filter.Ids is { } ids)
        {
            query = query.Where(e => ids.Contains(e.Id));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            var phone = filter.PhoneNumber;
            query = phone is null
                ? query.Where(e => e.FullName.Contains(text) || (e.Code != null && e.Code.Contains(text)) || (e.PhoneNumber != null && e.PhoneNumber.Contains(text)))
                : query.Where(e => e.FullName.Contains(text) || (e.Code != null && e.Code.Contains(text))
                    || (e.PhoneNumber != null && (e.PhoneNumber.Contains(text) || e.PhoneNumber == phone)));
        }

        if (filter.StatusId is not null)
        {
            query = query.Where(e => e.StatusId == filter.StatusId);
        }

        if (filter.ZoneId is not null)
        {
            query = query.Where(e => e.WorkingZones.Any(w => w.ZoneId == filter.ZoneId));
        }

        if (filter.Type is { } type)
        {
            // The same expression the sort uses, compared with the type asked for.
            var current = CurrentType(filter.Today);
            query = query.Where(Expression.Lambda<Func<Employee, bool>>(
                Expression.Equal(current.Body, Expression.Constant(type, typeof(EmployeeType?))), current.Parameters));
        }

        if (filter.JobTypeId is { } jobTypeId)
        {
            query = query.Where(e => e.JobTypes.Any(j => j.JobTypeId == jobTypeId));
        }

        return query;
    }

    /// <summary>
    /// The type in force on <paramref name="today"/> as <c>EmployeeTypePeriod.TypeOn</c> reads it: the latest period from that
    /// day or before, else the first one. An expression, so that it becomes SQL.
    /// </summary>
    private static Expression<Func<Employee, EmployeeType?>> CurrentType(DateOnly today) =>
        e => e.TypePeriods.Where(p => p.EffectiveFrom <= today).OrderByDescending(p => p.EffectiveFrom).Select(p => (EmployeeType?)p.Type).FirstOrDefault()
            ?? e.TypePeriods.OrderBy(p => p.EffectiveFrom).Select(p => (EmployeeType?)p.Type).FirstOrDefault();

    private static IOrderedQueryable<Employee> Ordered(IQueryable<Employee> query, EmployeeSearchFilter filter, IReadOnlyCollection<Guid>? firstZoneIds)
    {
        var today = filter.Today;
        var desc = filter.Descending;
        var ordered = filter.Sort switch
        {
            EmployeeSort.Name => desc ? query.OrderByDescending(e => e.FullName) : query.OrderBy(e => e.FullName),
            EmployeeSort.Code => desc ? query.OrderByDescending(e => e.Code) : query.OrderBy(e => e.Code),
            EmployeeSort.Phone => desc ? query.OrderByDescending(e => e.PhoneNumber) : query.OrderBy(e => e.PhoneNumber),
            EmployeeSort.Type => desc ? query.OrderByDescending(CurrentType(today)) : query.OrderBy(CurrentType(today)),
            EmployeeSort.Status => desc ? query.OrderByDescending(e => e.StatusId) : query.OrderBy(e => e.StatusId),
            EmployeeSort.LastWorkingDay => desc ? query.OrderByDescending(e => e.LastWorkingDay) : query.OrderBy(e => e.LastWorkingDay),
            _ => firstZoneIds is null
                ? query.OrderBy(e => e.FullName)
                : query.OrderByDescending(e => e.WorkingZones.Any(w => firstZoneIds.Contains(w.ZoneId))).ThenBy(e => e.FullName),
        };
        return ordered.ThenBy(e => e.Id);
    }

    private static IQueryable<Employee> WithRows(IQueryable<Employee> query) => query
        .Include(e => e.WorkingZones).ThenInclude(w => w.Zone)
        .Include(e => e.JobTypes).ThenInclude(j => j.JobType)
        .Include(e => e.TypePeriods)
        .Include(e => e.Account)
        .AsSplitQuery();

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
