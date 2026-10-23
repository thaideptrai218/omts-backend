using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class EmployeeRepository(OmtsDbContext db) : IEmployeeRepository
{
    public Task<Employee?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Employees.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public Task<Employee?> GetForEditAsync(Guid id, CancellationToken cancellationToken) =>
        db.Employees
            .Include(e => e.WorkingZones).ThenInclude(w => w.Zone)
            .Include(e => e.Account)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public Task<Employee?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken) =>
        db.Employees.FirstOrDefaultAsync(e => e.AccountId == accountId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, Guid?>> GetIdsByAccountIdsAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken) =>
        await db.Employees.AsNoTracking()
            .Where(e => e.AccountId != null && accountIds.Contains(e.AccountId.Value))
            .ToDictionaryAsync(e => e.AccountId!.Value, e => (Guid?)e.Id, cancellationToken);

    public async Task<(IReadOnlyList<Employee> Items, int Total)> SearchAsync(
        string? search, int? statusId, Guid? zoneId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Employees.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            // Phones are stored as 0xxxxxxxxx: a search typed as +84… or 84… matches them too.
            var phone = text.StartsWith("+84", StringComparison.Ordinal) ? $"0{text[3..]}" : text;
            query = query.Where(e => e.FullName.Contains(text) || (e.PhoneNumber != null && (e.PhoneNumber.Contains(text) || e.PhoneNumber.Contains(phone))));
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
        var items = await query
            .Include(e => e.WorkingZones).ThenInclude(w => w.Zone)
            .Include(e => e.Account)
            .OrderBy(e => e.FullName)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<Employee?> FindSamePersonAsync(string fullName, string phoneNumber, Guid? exceptEmployeeId, CancellationToken cancellationToken)
    {
        var name = fullName.ToUpperInvariant();
        return db.Employees.AsNoTracking().FirstOrDefaultAsync(
            e => e.PhoneNumber == phoneNumber && e.FullName.ToUpper() == name && e.Id != exceptEmployeeId, cancellationToken);
    }

    public async Task<LaborRecordsAfter> CountLaborRecordsAfterAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken) =>
        new(
            await db.AttendanceRecords.CountAsync(a => a.EmployeeId == employeeId && a.WorkDate > day, cancellationToken),
            await db.WorkAssignments.CountAsync(w => w.EmployeeId == employeeId && w.WorkDate > day && w.CancelledAt == null, cancellationToken));

    public void Add(Employee employee) => db.Employees.Add(employee);

    public void AddWorkingZone(EmployeeWorkingZone workingZone) => db.EmployeeWorkingZones.Add(workingZone);

    public void RemoveWorkingZone(EmployeeWorkingZone workingZone) => db.EmployeeWorkingZones.Remove(workingZone);
}
