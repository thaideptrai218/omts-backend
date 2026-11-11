using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Employees.EmployeesV2QaKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.EmployeeAppKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-03 and BR-LABOR-02: who issues a login code, for whom, what the code is and where it goes (and does not go).</summary>
public class EmployeeLoginCodeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task The_zone_manager_of_the_employee_and_the_owner_get_a_code_with_its_expiry_and_the_phone_to_call()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);

        foreach (var issuer in new[] { world.Manager, world.Owner })
        {
            var before = DateTimeOffset.UtcNow;
            var issued = await (await issuer.PostAsync($"/api/employees/{worker.Id}/login-codes", null)).JsonAsync(HttpStatusCode.Created);

            Assert.Matches("^[0-9]{6}$", issued.GetProperty("code").GetString()!);
            Assert.Equal(worker.Phone, issued.GetProperty("phoneNumber").GetString());
            Assert.Equal(worker.Name, issued.GetProperty("employeeName").GetString());
            Assert.Equal("MSG-LABOR-08", issued.GetProperty("messageCode").GetString());
            Assert.Contains(issued.GetProperty("code").GetString()!, issued.GetProperty("message").GetString());
            var lifetime = issued.GetProperty("expiresAt").GetDateTimeOffset() - before;
            Assert.InRange(lifetime, TimeSpan.FromMinutes(4.5), TimeSpan.FromMinutes(5.5));
        }
    }

    [Fact]
    public async Task Others_get_no_code_not_even_a_manager_of_another_zone_or_an_accountant()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var otherZone = await factory.AddZoneAsync(NewCode());
        var otherManager = (await StaffAsync(factory, (StaffRole.ZoneManager, otherZone))).Client;
        var accountant = (await StaffAsync(factory, (StaffRole.Accountant, null))).Client;
        var mixed = (await StaffAsync(factory, (StaffRole.ZoneManager, otherZone), (StaffRole.Accountant, world.Zone))).Client;

        foreach (var client in new[] { otherManager, accountant, mixed })
        {
            var refused = await client.PostAsync($"/api/employees/{worker.Id}/login-codes", null);

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("MSG-GEN-08", await refused.ErrorCodeAsync());
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsync($"/api/employees/{worker.Id}/login-codes", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.Owner.PostAsync($"/api/employees/{Guid.NewGuid()}/login-codes", null)).StatusCode);
        Assert.Empty(await factory.CodesAsync(worker.Id));
    }

    [Fact]
    public async Task An_employee_who_is_not_employed_or_has_no_phone_number_gets_no_code()
    {
        var world = await WorldAsync(factory);
        var left = await AddWorkerAsync(world);
        await EndAsync(world.Owner, left.Id);
        var endedButNotFlipped = await AddWorkerAsync(world);
        await factory.UpdateAsync<Employee>(endedButNotFlipped.Id, e => e.LastWorkingDay = Today().AddDays(-1));
        var noPhone = await AddWorkerAsync(world, withoutPhone: true);

        foreach (var (worker, reason) in new[] { (left, "Left"), (endedButNotFlipped, "Left"), (noPhone, "NoPhone") })
        {
            var refused = await world.Manager.PostAsync($"/api/employees/{worker.Id}/login-codes", null);

            var problem = await refused.JsonAsync(HttpStatusCode.Conflict);
            Assert.Equal("MSG-LABOR-09", problem.GetProperty("code").GetString());
            Assert.Equal(reason, problem.GetProperty("reason").GetString());
            Assert.Empty(await factory.CodesAsync(worker.Id));
        }

        // The last working day is today: still Employed, so the code is issued.
        var lastDay = await AddWorkerAsync(world);
        await EndAsync(world.Owner, lastDay.Id, Today());
        Assert.Matches("^[0-9]{6}$", await IssueCodeAsync(world.Manager, lastDay.Id));
    }

    [Fact]
    public async Task A_new_code_cancels_the_earlier_one_and_only_the_new_one_logs_in()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var first = await IssueCodeAsync(world.Manager, worker.Id);
        var second = await IssueCodeAsync(world.Owner, worker.Id);
        var phone = factory.CreateClient();

        var withFirst = await LoginWithCodeAsync(phone, worker.Phone, first);
        var withSecond = await LoginWithCodeAsync(phone, worker.Phone, second);

        if (first != second)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, withFirst.StatusCode);
            Assert.Equal("MSG-LABOR-19", await withFirst.ErrorCodeAsync());
        }

        Assert.Equal(HttpStatusCode.OK, withSecond.StatusCode);
        var rows = await factory.CodesAsync(worker.Id);
        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows[0].CancelledAt);
        Assert.Null(rows[0].ConsumedAt);
        Assert.NotNull(rows[1].ConsumedAt);
    }

    [Fact]
    public async Task The_code_lives_five_minutes_and_is_stored_only_as_a_hash()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var code = await IssueCodeAsync(world.Manager, worker.Id);

        var row = Assert.Single(await factory.CodesAsync(worker.Id));

        Assert.Equal(TimeSpan.FromMinutes(5), row.ExpiresAt - row.IssuedAt);
        Assert.Equal(world.ManagerAccount.Id, row.IssuedByAccountId);
        Assert.Equal(64, row.CodeHash.Length);
        Assert.DoesNotContain(code, row.CodeHash);
        Assert.Equal(0, row.FailedAttempts);
    }

    // BR-LABOR-02, GBR-02: the audit log says a code was issued, by whom and when it expires, never the code.
    [Fact]
    public async Task The_audit_log_and_the_logs_never_hold_the_code()
    {
        using var host = new AuthApiFactory();
        var world = await WorldAsync(host);
        var worker = await AddWorkerAsync(world);

        var code = await IssueCodeAsync(world.Manager, worker.Id);
        await LoginWithCodeAsync(host.CreateClient(), worker.Phone, code);

        var issued = Assert.Single(await host.AuditAsync(worker.Id, "Employee.LoginCodeIssued"));
        Assert.Equal(world.ManagerAccount.Id, issued.ActorAccountId);
        Assert.Null(issued.ActorEmployeeId);
        var everyRow = await host.QueryAsync(db => db.AuditEvents.AsNoTracking().ToListAsync());
        Assert.DoesNotContain(everyRow, e => $"{e.Before}{e.After}{e.Reason}{e.RecordCode}".Contains(code, StringComparison.Ordinal));
        Assert.DoesNotContain(host.Logs.Lines, line => line.Contains(code, StringComparison.Ordinal));
        Assert.DoesNotContain(code, await host.QueryAsync(db => db.EmployeeLoginCodes.AsNoTracking().Select(c => c.CodeHash).SingleAsync()));
    }

    // LABOR-03 step 4: the issuer's own notification list, a normal type; never a device or a phone of the employee.
    [Fact]
    public async Task The_code_goes_to_the_issuers_notification_list_and_never_to_the_employee()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);

        var code = await IssueCodeAsync(world.Manager, worker.Id);

        var kept = await factory.QueryAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.RecipientAccountId == world.ManagerAccount.Id && n.Type == NotificationTypes.EmployeeLoginCode).ToListAsync());
        var row = Assert.Single(kept);
        Assert.Equal("MSG-LABOR-08", row.MessageCode);
        Assert.Contains(code, row.Body);
        Assert.False(row.IsMandatory);
        Assert.Empty(await factory.QueryAsync(db => db.Notifications.AsNoTracking().Where(n => n.RecipientAccountId == world.OwnerAccount.Id && n.Type == NotificationTypes.EmployeeLoginCode).ToListAsync()));
        var push = factory.Services.GetRequiredService<FakePushSender>().Inbox.List(world.ManagerAccount.Id.ToString()).Single(m => m.NotificationId == row.Id);
        Assert.Equal("MSG-LABOR-08", push.MessageCode);
        Assert.Empty(factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List(worker.Phone));
        Assert.Empty(factory.Services.GetRequiredService<FakePushSender>().Inbox.List(worker.Id.ToString()));
    }

    // Preferences (ACCOUNT-07): a normal type, so the issuer can turn its push off and the row is still kept (BR-ACCOUNT-12).
    [Fact]
    public async Task The_issuer_can_turn_the_push_of_the_code_off_and_the_row_is_still_kept()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var off = await world.Manager.PutAsJsonAsync("/api/me/notification-preferences", new { items = new[] { new { type = NotificationTypes.EmployeeLoginCode, push = false } } });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        await IssueCodeAsync(world.Manager, worker.Id);

        Assert.Single(await factory.QueryAsync(db => db.Notifications.AsNoTracking().Where(n => n.RecipientAccountId == world.ManagerAccount.Id && n.Type == NotificationTypes.EmployeeLoginCode).ToListAsync()));
        Assert.Empty(factory.Services.GetRequiredService<FakePushSender>().Inbox.List(world.ManagerAccount.Id.ToString()).Where(m => m.Text.StartsWith(NotificationTypes.EmployeeLoginCode, StringComparison.Ordinal)));
    }
}
