using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees;

/// <summary>Puts validated fields on a profile: the plain values, and the rows of its working zones, job types and type periods.</summary>
internal static class EmployeeProfileChanges
{
    public static void ApplyFields(Employee employee, EmployeeInput input)
    {
        employee.FullName = input.FullName;
        employee.PhoneNumber = input.PhoneNumber;
        employee.Birthday = input.Birthday;
        employee.Address = input.Address;
        employee.Area = input.Area;
        employee.Ward = input.Ward;
        employee.OtherInformation = input.OtherInformation;
        employee.Notes = input.Notes;
        employee.BankAccount = input.BankAccount;
    }

    /// <summary>
    /// The working zones the profile keeps after an edit: those the caller sent, and those outside the caller's zones, which
    /// stay whatever was sent (LABOR-01.1 step 2).
    /// </summary>
    public static List<Guid> ZonesAfterEdit(Employee employee, IReadOnlyList<Guid> sent, EmployeeCaller caller) =>
        employee.WorkingZones.Select(w => w.ZoneId).Where(z => !caller.HasZone(z)).Union(sent).ToList();

    /// <summary>Whether anything changed: the working zones are rows of their own.</summary>
    public static bool SyncZones(IEmployeeRepository employees, Employee employee, IReadOnlyCollection<Guid> zoneIds)
    {
        var removed = employee.WorkingZones.Where(w => !zoneIds.Contains(w.ZoneId)).ToList();
        var added = zoneIds.Where(z => employee.WorkingZones.All(w => w.ZoneId != z)).ToList();
        removed.ForEach(employees.RemoveWorkingZone);
        added.ForEach(z => employees.AddWorkingZone(new EmployeeWorkingZone { EmployeeId = employee.Id, ZoneId = z }));
        return removed.Count + added.Count > 0;
    }

    /// <summary>Whether anything changed.</summary>
    public static bool SyncJobTypes(IEmployeeRepository employees, Employee employee, IReadOnlyCollection<Guid> jobTypeIds)
    {
        var removed = employee.JobTypes.Where(j => !jobTypeIds.Contains(j.JobTypeId)).ToList();
        var added = jobTypeIds.Where(id => employee.JobTypes.All(j => j.JobTypeId != id)).ToList();
        removed.ForEach(employees.RemoveJobType);
        added.ForEach(id => employees.AddJobType(new EmployeeJobType { EmployeeId = employee.Id, JobTypeId = id }));
        return removed.Count + added.Count > 0;
    }

    /// <summary>Whether the type on its date is a change: the type already in force on that date changes nothing.</summary>
    public static bool IsTypeChange(Employee employee, EmployeeType type, DateOnly from) =>
        employee.TypePeriods.Count == 0 || EmployeeTypePeriod.TypeOn(employee.TypePeriods, from) != type;

    /// <summary>
    /// A new type from its date; the earlier types stay (BR-LABOR-01). Another type entered with the date of a period replaces
    /// that period (the audit row keeps the old one). LABOR-01.1.E1, the pay period lock, is checked with the other fields, in
    /// <see cref="EmployeeProfileValidator"/>. Whether anything changed.
    /// </summary>
    public static bool ChangeType(IEmployeeRepository employees, Employee employee, EmployeeType type, DateOnly from)
    {
        if (!IsTypeChange(employee, type, from))
        {
            return false;
        }

        if (employee.TypePeriods.FirstOrDefault(p => p.EffectiveFrom == from) is { } sameDate)
        {
            sameDate.Type = type;
        }
        else
        {
            employees.AddTypePeriod(new EmployeeTypePeriod { EmployeeId = employee.Id, Type = type, EffectiveFrom = from });
        }

        return true;
    }

    /// <summary>The audit snapshot; the bank number is masked, as everywhere in the audit log.</summary>
    /// <param name="accountNumberBefore">On the snapshot after an edit: the real number before it, see <see cref="BankSnapshot"/>.</param>
    public static object Snapshot(Employee e, IReadOnlyCollection<Guid> zoneIds, IReadOnlyCollection<Guid> jobTypeIds, string? accountNumberBefore = null) => new
    {
        e.FullName, e.PhoneNumber, e.Birthday, e.Address, e.Area, e.Ward, e.OtherInformation, e.Notes, e.AccountId,
        WorkingZoneIds = zoneIds.Order().ToList(),
        JobTypeIds = jobTypeIds.Order().ToList(),
        TypeHistory = e.TypePeriods.OrderBy(p => p.EffectiveFrom).Select(p => new { p.Type, p.EffectiveFrom }).ToList(),
        BankAccount = BankSnapshot(e.BankAccount, accountNumberBefore),
    };

    /// <summary>
    /// Two numbers with the same last four digits read the same masked. The real numbers are compared here, and a changed one
    /// is marked <c>AccountNumberChanged: true</c> on the row's After, so the diff keeps the field and a reader can tell
    /// (GBR-02). Nothing else derived from the number is written: a hash of 6 to 20 digits could be reversed.
    /// </summary>
    private static object? BankSnapshot(BankAccountInfo? bank, string? accountNumberBefore)
    {
        if (bank is null)
        {
            return null;
        }

        var masked = EmployeeInput.MaskAccountNumber(bank.AccountNumber);
        return accountNumberBefore is not null && accountNumberBefore != bank.AccountNumber
            ? new { bank.BankName, AccountNumber = masked, bank.AccountHolderName, AccountNumberChanged = true }
            : new { bank.BankName, AccountNumber = masked, bank.AccountHolderName };
    }
}
