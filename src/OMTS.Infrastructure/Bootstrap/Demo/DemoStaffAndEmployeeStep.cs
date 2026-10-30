using OMTS.Application.Features.Employees.Dtos;
using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Employees;
using OMTS.Application.Common.Validation;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// LAB-01 + ACS-09: the demo employees, and the staff accounts (with zone–role grants) created together with
/// them through <see cref="EmployeeService"/>. The service issues random temporary passwords; this step then
/// gives each account the fixed demo password and its intended login branch (see <see cref="DemoLoginKind"/>).
/// One employee has Left. Matched by employee phone and by username.
/// </summary>
internal sealed class DemoStaffAndEmployeeStep : IDemoSeedStep
{
    // "Issued" far in the future so the 24-hour expiry of a temporary password never applies to the demo account.
    private static readonly DateTimeOffset NeverExpiresIssuedAt = new(2999, 12, 31, 0, 0, 0, TimeSpan.Zero);

    public string Name => "employees and staff accounts";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<EmployeeService>();
        var hash = context.Get<IPasswordHasher>().Hash(DemoSeedContext.Password);
        var created = 0;
        foreach (var plan in DemoEmployeePlans.All)
        {
            var phone = PhoneNumbers.Normalize(plan.Phone)!;
            var employee = await context.Db.Employees.FirstOrDefaultAsync(e => e.PhoneNumber == phone, cancellationToken);
            if (employee is null)
            {
                var saved = DemoSeedContext.Ensure(await service.CreateAsync(await RequestAsync(context, plan, cancellationToken), cancellationToken), $"employee {plan.FullName}");
                employee = await context.Db.Employees.FirstAsync(e => e.Id == saved.Employee.Id, cancellationToken);
                created++;
            }

            if (plan.Username is not null)
            {
                await SetDemoCredentialsAsync(context, plan, hash, cancellationToken);
            }

            if (plan.LeftDaysAgo is { } daysAgo && employee.StatusId == StatusIds.Employee.Working)
            {
                DemoSeedContext.Ensure(
                    await service.EndEmploymentAsync(employee.Id, new EndEmploymentRequest(context.Today.AddDays(-daysAgo)), cancellationToken),
                    $"ending employment of {plan.FullName}");
            }
        }

        return created;
    }

    private static async Task<SaveEmployeeRequest> RequestAsync(DemoSeedContext context, DemoEmployeePlan plan, CancellationToken cancellationToken)
    {
        var zoneIds = await context.Db.FarmingZones
            .Where(z => plan.WorkingZones.Contains(z.Code)).Select(z => z.Id).ToListAsync(cancellationToken);

        NewEmployeeAccount? account = null;
        if (plan.Username is not null)
        {
            var roleId = await context.Db.Roles.Where(r => r.Name == plan.RoleName).Select(r => r.Id).SingleAsync(cancellationToken);
            Guid? grantZoneId = plan.GrantZone is null
                ? null
                : await context.Db.FarmingZones.Where(z => z.Code == plan.GrantZone).Select(z => z.Id).SingleAsync(cancellationToken);
            account = new NewEmployeeAccount(
                plan.Username, null, null, null,
                [new GrantDraft(grantZoneId, roleId, context.Today.AddDays(plan.GrantFromDays), plan.GrantToDays is { } to ? context.Today.AddDays(to) : null)]);
        }

        return new SaveEmployeeRequest(
            plan.FullName, plan.Phone, plan.Birthday, $"{plan.Ward}, Huyện Vân Đồn, Tỉnh Quảng Ninh", "Vân Đồn", plan.Ward,
            null, "Dữ liệu minh họa", zoneIds,
            new EmployeeBankAccountInput(plan.BankName, plan.BankAccountNumber, plan.FullName), null, account);
    }

    /// <summary>
    /// Replaces the random temporary password of the account's creation with the demo one, once per account: an
    /// earlier run's audit row means the demo login is set, and a later reset by the Owner or a changed password
    /// stays. Recorded in the audit log without any secret.
    /// </summary>
    private static async Task SetDemoCredentialsAsync(DemoSeedContext context, DemoEmployeePlan plan, string hash, CancellationToken cancellationToken)
    {
        var account = await context.Db.Accounts.SingleAsync(a => a.Username == plan.Username, cancellationToken);
        if (!account.MustChangePassword || await context.DemoLoginAlreadySetAsync(account.Id, cancellationToken))
        {
            return;
        }

        var mustChange = plan.Login == DemoLoginKind.MustChangePassword;
        account.PasswordHash = hash;
        account.MustChangePassword = mustChange;
        account.TemporaryPasswordIssuedAt = mustChange ? NeverExpiresIssuedAt : null;
        account.PhoneVerifiedAt = plan.Login == DemoLoginKind.PhoneUnverified ? null : context.Now;
        context.Get<AuditLogService>().Record(new AuditEntry(DemoSeedContext.DemoLoginSetAction, nameof(Account))
        {
            RecordId = account.Id,
            RecordCode = account.Username,
            After = new { account.Username, account.MustChangePassword, PhoneVerified = account.PhoneVerifiedAt is not null, plan.Login },
        });
        await context.Db.SaveChangesAsync(cancellationToken);
    }
}
