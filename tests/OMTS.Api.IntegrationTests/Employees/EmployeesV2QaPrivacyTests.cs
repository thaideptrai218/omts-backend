using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Employees.EmployeesV2QaKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>
/// QA of OMTS-227, who reads what. BR-LABOR-01 and BR-FARM-08: every Zone Manager sees every employee; §I.4.4: Accountant,
/// Trader and anonymous have no LABOR-01. Hiding the personal fields from a manager who does not maintain the employee is the
/// branch's own rule (PM question): the SRS neither asks for it nor forbids it (NFR-11 "shown only to authorized users").
/// </summary>
public class EmployeesV2QaPrivacyTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Every_kind_of_caller_reads_what_the_rule_says_on_the_detail_and_the_list()
    {
        var (owner, zoneA, _) = await SetupAsync(factory);
        var zoneB = await factory.AddZoneAsync(NewCode());
        var name = $"Privacy {NewCode()}";
        var id = IdOf(await CreateAsync(owner, FullProfile(name, NewPhone(), zoneA)));
        var cases = new (string Who, HttpClient Client, bool Maintains)[]
        {
            ("Owner", owner, true),
            ("Manager of the zone", (await StaffAsync(factory, (StaffRole.ZoneManager, zoneA))).Client, true),
            ("Manager of both zones", (await StaffAsync(factory, (StaffRole.ZoneManager, zoneB), (StaffRole.ZoneManager, zoneA))).Client, true),
            ("All zones manager", (await StaffAsync(factory, (StaffRole.ZoneManager, null))).Client, true),
            ("Manager of another zone", (await StaffAsync(factory, (StaffRole.ZoneManager, zoneB))).Client, false),
            ("Manager B, Accountant A", (await StaffAsync(factory, (StaffRole.ZoneManager, zoneB), (StaffRole.Accountant, zoneA))).Client, false),
            ("Manager B, Accountant All", (await StaffAsync(factory, (StaffRole.ZoneManager, zoneB), (StaffRole.Accountant, null))).Client, false),
        };

        foreach (var (who, client, maintains) in cases)
        {
            var view = await (await client.GetAsync($"/api/employees/{id}")).JsonAsync(HttpStatusCode.OK);
            Assert.True(view.GetProperty("canMaintain").GetBoolean() == maintains, who);
            AssertPersonalFields(view, maintains);
            Assert.Equal(name, view.GetProperty("fullName").GetString());
            Assert.Equal(1, view.GetProperty("workingZones").GetArrayLength());
            Assert.Equal(who == "Owner" ? JsonValueKind.Null : JsonValueKind.Null, view.GetProperty("account").ValueKind);

            var row = (await (await client.GetAsync($"/api/employees?search={Uri.EscapeDataString(name)}")).JsonAsync(HttpStatusCode.OK)).GetProperty("items")[0];
            Assert.True(row.GetProperty("canMaintain").GetBoolean() == maintains, who);
            Assert.All(PersonalFields, field => Assert.False(row.TryGetProperty(field, out _), $"{field} is on the list row for {who}."));
        }
    }

    // §I.4.4: LABOR-01 is No for the Accountant and the Trader, also with an All zones pair; a mixed pair gives nothing more.
    [Fact]
    public async Task Accountants_traders_anonymous_and_a_revoked_pair_read_nothing()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var id = IdOf(await CreateAsync(owner, FullProfile($"Refuse {NewCode()}", NewPhone(), zone)));
        var traderPassword = NewPassword();
        var trader = await factory.AddAccountAsync(NewCode(), traderPassword, SubjectType.Trader);
        var (revoked, revokedAccount) = await StaffAsync(factory, (StaffRole.ZoneManager, zone));
        var refused = new[]
        {
            (await StaffAsync(factory, (StaffRole.Accountant, zone))).Client,
            (await StaffAsync(factory, (StaffRole.Accountant, null))).Client,
            await factory.LoginAsync(trader.Username, traderPassword),
            revoked,
        };
        Assert.Equal(HttpStatusCode.OK, (await revoked.GetAsync($"/api/employees/{id}")).StatusCode);
        var grant = await factory.QueryAsync(db => db.AccessGrants.Where(g => g.AccountId == revokedAccount.Id).Select(g => g.Id).SingleAsync());
        await (await owner.PostAsJsonAsync($"/api/accounts/{revokedAccount.Id}/grants/{grant}/revoke", new { reason = "Thu hồi" })).JsonAsync(HttpStatusCode.OK);

        foreach (var client in refused)
        {
            Assert.Equal("MSG-GEN-08", await (await client.GetAsync($"/api/employees/{id}")).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.GetAsync("/api/employees")).ErrorCodeAsync());
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/employees/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/employees")).StatusCode);
    }

    // GFR-02: a revoke applies from the next action, also when the account keeps a pair elsewhere.
    [Fact]
    public async Task Losing_the_pair_of_the_zone_ends_maintaining_from_the_next_action_and_a_new_pair_gives_it_back()
    {
        var (owner, zoneA, _) = await SetupAsync(factory);
        var zoneB = await factory.AddZoneAsync(NewCode());
        var (name, phone) = ($"Revoke {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, FullProfile(name, phone, zoneA));
        var id = IdOf(saved);
        var (manager, account) = await StaffAsync(factory, (StaffRole.ZoneManager, zoneA), (StaffRole.ZoneManager, zoneB));
        AssertPersonalFields(await (await manager.GetAsync($"/api/employees/{id}")).JsonAsync(HttpStatusCode.OK), visible: true);
        var pairA = await factory.QueryAsync(db => db.AccessGrants.Where(g => g.AccountId == account.Id && g.ZoneId == zoneA.Id).Select(g => g.Id).SingleAsync());

        await (await owner.PostAsJsonAsync($"/api/accounts/{account.Id}/grants/{pairA}/revoke", new { reason = "Thu hồi" })).JsonAsync(HttpStatusCode.OK);

        var after = await (await manager.GetAsync($"/api/employees/{id}")).JsonAsync(HttpStatusCode.OK);
        Assert.False(after.GetProperty("canMaintain").GetBoolean());
        AssertPersonalFields(after, visible: false);
        Assert.Equal("MSG-GEN-08", await (await manager.PutAsJsonAsync($"/api/employees/{id}", FullProfile(name, phone, zoneB))).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await manager.PostEndAsync(id, await EndBodyAsync(owner, id, Today()))).ErrorCodeAsync());

        await factory.GrantAsync(account, StaffRole.ZoneManager, zoneA);
        AssertPersonalFields(await (await manager.GetAsync($"/api/employees/{id}")).JsonAsync(HttpStatusCode.OK), visible: true);
    }

    [Fact]
    public async Task The_hidden_fields_cannot_be_probed_through_search_or_the_answer_of_a_refusal()
    {
        var (owner, zoneA, _) = await SetupAsync(factory);
        var zoneB = await factory.AddZoneAsync(NewCode());
        var phone = NewPhone();
        var id = IdOf(await CreateAsync(owner, FullProfile($"Probe {NewCode()}", phone, zoneA)));
        var (other, _) = await StaffAsync(factory, (StaffRole.ZoneManager, zoneB));

        foreach (var text in new[] { "999900000099", "Riêng tư ghi chú", "Riêng tư địa chỉ" })
        {
            var found = await (await other.GetAsync($"/api/employees?search={Uri.EscapeDataString(text)}")).JsonAsync(HttpStatusCode.OK);
            Assert.Equal(0, found.GetProperty("total").GetInt32());
        }

        var taken = await other.PostAsJsonAsync("/api/employees", ApprovedProfile($"Same {NewCode()}", phone, zoneB.Id));
        var body = await taken.JsonAsync(HttpStatusCode.Conflict);

        Assert.Equal("MSG-LABOR-03", body.GetProperty("code").GetString());
        Assert.Equal(id, body.GetProperty("existingEmployeeId").GetGuid());
        Assert.DoesNotContain("Riêng tư", body.GetRawText());
        Assert.DoesNotContain("999900000099", body.GetRawText());
    }

    // ACCOUNT-10 is Owner only; the audit rows hold the personal fields, with the bank number masked.
    [Fact]
    public async Task Audit_rows_are_for_the_owner_only_and_never_hold_the_whole_bank_number()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (name, phone) = ($"Audit {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, FullProfile(name, phone, zone));
        var id = IdOf(saved);
        var (manager, _) = await StaffAsync(factory, (StaffRole.ZoneManager, zone));
        var rowId = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == id).Select(e => e.Id).FirstAsync());

        foreach (var url in new[] { "/api/audit-events", $"/api/audit-events/{rowId}", $"/api/audit-events/{rowId}/record" })
        {
            Assert.Equal("MSG-GEN-08", await (await manager.GetAsync(url)).ErrorCodeAsync());
        }

        var rows = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == id).Select(e => (e.Before ?? string.Empty) + (e.After ?? string.Empty)).ToListAsync());
        Assert.NotEmpty(rows);
        Assert.DoesNotContain(rows, r => r.Contains("999900000099"));
        Assert.Contains(rows, r => r.Contains("********0099"));
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Contains("999900000099"));
    }

    // LABOR-01.3 step 5, BR-LABOR-04: the Owners' list, not pushed, whoever ends the employment.
    [Fact]
    public async Task The_lock_reminder_goes_to_active_owners_only_is_never_pushed_and_holds_no_personal_field()
    {
        using var host = new AuthApiFactory();
        var (owner, zone, _) = await SetupAsync(host);
        var otherOwner = await host.AddAccountAsync(NewCode(), NewPassword());
        var lockedOwner = await host.AddAccountAsync(NewCode(), NewPassword(), SubjectType.CooperativeOwner, a => a.StatusId = OMTS.Domain.Constants.StatusIds.Account.Locked);
        var staff = await host.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff);
        var id = IdOf(await CreateAsync(owner, FullProfile($"Remind {NewCode()}", staff.PhoneNumber, zone)));
        var (manager, managerAccount) = await StaffAsync(host, (StaffRole.ZoneManager, zone));

        await (await manager.PostEndAsync(id, await EndBodyAsync(owner, id, Today().AddDays(-1)))).JsonAsync(HttpStatusCode.OK);

        var rows = await host.QueryAsync(db => db.Notifications.AsNoTracking().Where(n => n.Type == "STAFF_ACCOUNT_LOCK_REMINDER").ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, n => n.RecipientAccountId == lockedOwner.Id || n.RecipientAccountId == managerAccount.Id || n.RecipientAccountId == staff.Id);
        Assert.Contains(rows, n => n.RecipientAccountId == otherOwner.Id);
        Assert.All(rows, n => Assert.Equal("MSG-LABOR-02", n.MessageCode));
        Assert.All(rows, n => Assert.DoesNotContain("Riêng tư", n.Body));
        Assert.All(rows, n => Assert.DoesNotContain("999900000099", n.Body));
        Assert.All(rows, n => Assert.DoesNotContain(staff.PhoneNumber!, n.Body));
        Assert.Empty(host.Services.GetRequiredService<FakePushSender>().Inbox.List());

        // ACCOUNT-07 does not offer it, and a crafted preference changes nothing.
        var preferences = await (await owner.GetAsync("/api/me/notification-preferences")).JsonAsync(HttpStatusCode.OK);
        Assert.DoesNotContain("STAFF_ACCOUNT_LOCK_REMINDER", preferences.GetRawText());
        var crafted = await owner.PutAsJsonAsync("/api/me/notification-preferences", new { items = new[] { new { type = "STAFF_ACCOUNT_LOCK_REMINDER", push = true } } });
        Assert.True((int)crafted.StatusCode < 500);
        var second = await host.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff);
        var again = IdOf(await CreateAsync(owner, Profile($"Again {NewCode()}", second.PhoneNumber, zone.Id)));
        await EndAsync(owner, again);
        Assert.Empty(host.Services.GetRequiredService<FakePushSender>().Inbox.List());

        // The list and the opening of the record work for the Owner; no one else reaches the notification.
        var list = await (await owner.GetAsync("/api/me/notifications")).JsonAsync(HttpStatusCode.OK);
        var mine = list.GetProperty("items").EnumerateArray().First(n => n.GetProperty("type").GetString() == "STAFF_ACCOUNT_LOCK_REMINDER");
        var notificationId = mine.GetProperty("id").GetGuid();
        var opened = await owner.GetAsync($"/api/me/notifications/{notificationId}/open");
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/me/notifications/{notificationId}/open")).StatusCode);
        Assert.Equal(0, (await (await manager.GetAsync("/api/me/notifications")).JsonAsync(HttpStatusCode.OK)).GetProperty("items").GetArrayLength());
    }

    // LABOR-01.2 lets any manager add their zone, so the hiding is no barrier: recorded here for the PM question.
    [Fact]
    public async Task Add_my_zone_opens_the_personal_fields_to_any_zone_manager_and_is_audited()
    {
        var (owner, zoneA, _) = await SetupAsync(factory);
        var zoneB = await factory.AddZoneAsync(NewCode());
        var id = IdOf(await CreateAsync(owner, FullProfile($"Unlock {NewCode()}", NewPhone(), zoneA)));
        var (other, otherAccount) = await StaffAsync(factory, (StaffRole.ZoneManager, zoneB));

        var added = await (await other.PostAsJsonAsync($"/api/employees/{id}/working-zones", new { zoneId = zoneB.Id })).JsonAsync(HttpStatusCode.OK);

        AssertPersonalFields(added.GetProperty("employee"), visible: true);
        var audit = await factory.QueryAsync(db => db.AuditEvents.AsNoTracking().SingleAsync(e => e.RecordId == id && e.Action == "Employee.WorkingZoneAdded"));
        Assert.Equal(otherAccount.Id, audit.ActorAccountId);
    }
}
