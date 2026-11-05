using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Employees;

/// <summary>
/// What a caller sees of an employee: every Zone Manager sees every employee (BR-LABOR-01), and bank details and the other
/// personal fields go only to those who maintain the employee. The linked staff account is for the Cooperative Owner.
/// </summary>
public sealed class EmployeeViewBuilder(IStatusCatalog statusCatalog, TimeProvider timeProvider)
{
    public EmployeeView View(Employee e, EmployeeCaller caller, bool hasStaffAccount)
    {
        var maintains = caller.Maintains(e);
        return new EmployeeView(
            e.Id,
            e.FullName,
            e.PhoneNumber,
            EmployeeTypePeriod.TypeOn(e.TypePeriods, timeProvider.Today()),
            e.TypePeriods.OrderBy(p => p.EffectiveFrom).Select(p => new EmployeeTypePeriodView(p.Type, p.EffectiveFrom)).ToList(),
            JobTypes(e),
            Zones(e),
            statusCatalog.DisplayNameOf(e.StatusId),
            e.LastWorkingDay,
            maintains,
            hasStaffAccount,
            maintains ? e.Birthday : null,
            maintains ? e.Address : null,
            maintains ? e.Area : null,
            maintains ? e.Ward : null,
            maintains ? e.OtherInformation : null,
            maintains ? e.Notes : null,
            maintains && e.BankAccount is { } bank ? new EmployeeBankAccountView(bank.BankName, bank.AccountNumber, bank.AccountHolderName) : null,
            Linked(e, caller),
            RowVersionGuard.Encode(e.RowVersion));
    }

    public EmployeeListItem ListItem(Employee e, EmployeeCaller caller) => new(
        e.Id,
        e.FullName,
        e.PhoneNumber,
        EmployeeTypePeriod.TypeOn(e.TypePeriods, timeProvider.Today()),
        JobTypes(e),
        Zones(e),
        statusCatalog.DisplayNameOf(e.StatusId),
        e.LastWorkingDay,
        caller.Maintains(e),
        Linked(e, caller),
        RowVersionGuard.Encode(e.RowVersion));

    private IReadOnlyList<JobTypeRef> JobTypes(Employee e) =>
        e.JobTypes.Where(j => j.JobType is not null)
            .Select(j => new JobTypeRef(j.JobTypeId, j.JobType.Name, statusCatalog.DisplayNameOf(j.JobType.StatusId)))
            .OrderBy(j => j.Name)
            .ToList();

    private static IReadOnlyList<ZoneRef> Zones(Employee e) =>
        e.WorkingZones.Where(w => w.Zone is not null).Select(w => new ZoneRef(w.ZoneId, w.Zone.Code, w.Zone.Name)).OrderBy(z => z.Code).ToList();

    private LinkedAccountView? Linked(Employee e, EmployeeCaller caller) =>
        caller.IsOwner && e.Account is { } account
            ? new LinkedAccountView(account.Id, account.Username, account.DisplayName, statusCatalog.DisplayNameOf(account.StatusId))
            : null;
}
