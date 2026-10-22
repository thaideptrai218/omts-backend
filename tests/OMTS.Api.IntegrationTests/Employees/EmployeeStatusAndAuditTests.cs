using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>LAB-01 end employment / reinstate rules (MSG11, MSG61, follow-ups), audit rows, PII in logs and the list.</summary>
public class EmployeeStatusAndAuditTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Work_assignment_after_the_end_date_blocks_but_cancelled_or_same_day_records_do_not()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var id = IdOf(await CreateAsync(owner, Profile($"Assigned {NewCode()}", NewPhone(), zone.Id)));
        var endDate = new DateOnly(2026, 10, 15);
        await factory.UseDbAsync(db =>
        {
            db.WorkAssignments.Add(new WorkAssignment { ZoneId = zone.Id, EmployeeId = id, Task = "Vệ sinh lồng", WorkDate = endDate.AddDays(2), PaymentType = WorkPaymentType.DailyWage });
            db.WorkAssignments.Add(new WorkAssignment { ZoneId = zone.Id, EmployeeId = id, Task = "Đã huỷ", WorkDate = endDate.AddDays(3), PaymentType = WorkPaymentType.DailyWage, CancelledAt = DateTimeOffset.UtcNow });
            db.AttendanceRecords.Add(new AttendanceRecord { EmployeeId = id, WorkDate = endDate });
        });

        var blocked = await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { endDate = "2026-10-15" });
        var problem = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        var statusAfterBlock = await factory.QueryAsync(db => db.Employees.Where(e => e.Id == id).Select(e => e.StatusId).SingleAsync());
        var ended = await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { endDate = "2026-10-17" });

        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("MSG11", problem.GetProperty("code").GetString());
        Assert.Equal("Cannot proceed because 1 records are still in use: attendance records (0), work assignments (1) after 15/10/2026. Please handle these records first.",
            problem.GetProperty("detail").GetString());
        Assert.Equal(StatusIds.Employee.Working, statusAfterBlock);
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        Assert.Equal(new DateOnly(2026, 10, 17), await factory.QueryAsync(db => db.Employees.Where(e => e.Id == id).Select(e => e.EmploymentEndDate).SingleAsync()));
        // Records already made stay unchanged.
        Assert.Equal(2, await factory.QueryAsync(db => db.WorkAssignments.CountAsync(w => w.EmployeeId == id)));
        Assert.Equal(1, await factory.QueryAsync(db => db.AttendanceRecords.CountAsync(r => r.EmployeeId == id)));
    }

    [Fact]
    public async Task Follow_ups_appear_only_for_a_linked_account_which_is_never_locked_automatically()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var plain = IdOf(await CreateAsync(owner, Profile($"Plain {NewCode()}", NewPhone(), zone.Id)));
        var username = $"nv{NewCode()}";
        var withAccount = IdOf(await CreateAsync(owner, Profile($"Acc {NewCode()}", NewPhone(), zone.Id, b => b["account"] = AccountBlock(username, zone, role))));
        var lockedStaff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, a => a.StatusId = StatusIds.Account.Locked);
        var withLocked = IdOf(await CreateAsync(owner, Profile($"Locked {NewCode()}", NewPhone(), zone.Id, b => b["accountId"] = lockedStaff.Id)));

        var plainEnded = await EndAsync(owner, plain);
        var plainBack = await (await owner.PostAsync($"/api/employees/{plain}/reinstate", null)).Content.ReadFromJsonAsync<JsonElement>();
        var accountEnded = await EndAsync(owner, withAccount);
        var lockedEnded = await EndAsync(owner, withLocked);
        var lockedBack = await (await owner.PostAsync($"/api/employees/{withLocked}/reinstate", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Null, plainEnded.GetProperty("followUp").ValueKind);
        Assert.Equal(JsonValueKind.Null, plainBack.GetProperty("followUp").ValueKind);
        var accountId = await factory.QueryAsync(db => db.Accounts.Where(a => a.Username == username).Select(a => a.Id).SingleAsync());
        Assert.Equal(accountId, accountEnded.GetProperty("followUp").GetProperty("accountId").GetGuid());
        Assert.Equal(StatusIds.Account.Active, await factory.QueryAsync(db => db.Accounts.Where(a => a.Id == accountId).Select(a => a.StatusId).SingleAsync()));
        Assert.Equal(JsonValueKind.Null, lockedEnded.GetProperty("followUp").ValueKind);
        Assert.Equal(("review-access", lockedStaff.Id), (lockedBack.GetProperty("followUp").GetProperty("action").GetString(), lockedBack.GetProperty("followUp").GetProperty("accountId").GetGuid()));
    }

    [Fact]
    public async Task Reinstating_an_employed_person_is_MSG61_and_status_changes_are_audited()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var id = IdOf(await CreateAsync(owner, Profile($"Status {NewCode()}", NewPhone(), zone.Id)));

        var notLeft = await owner.PostAsync($"/api/employees/{id}/reinstate", null);
        await EndAsync(owner, id, "2026-10-21");
        var back = await owner.PostAsync($"/api/employees/{id}/reinstate", null);

        Assert.Equal(HttpStatusCode.Conflict, notLeft.StatusCode);
        Assert.Equal("Cannot change Employee from “Employed” to “Employed”: the employee is already in this status.",
            (await notLeft.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        var changes = await factory.QueryAsync(db => db.AuditEvents.AsNoTracking()
            .Where(e => e.RecordId == id && e.Action == "Employee.StatusChanged").OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(2, changes.Count);
        Assert.Equal(("WORKING", "LEFT"), (StatusIn(changes[0].Before), StatusIn(changes[0].After)));
        Assert.Contains("2026-10-21", changes[0].Reason);
        Assert.Equal(("LEFT", "WORKING"), (StatusIn(changes[1].Before), StatusIn(changes[1].After)));
    }

    [Fact]
    public async Task Update_audits_only_changed_fields_with_masked_bank_numbers_and_logs_hold_no_personal_data()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var phone = NewPhone();
        var saved = await CreateAsync(owner, Profile($"Audit {NewCode()}", phone, zone.Id, b => b["bankAccount"] = Bank("11112222333344")));
        var id = IdOf(saved);

        var updated = await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(saved.GetProperty("employee").GetProperty("fullName").GetString()!, phone, zone.Id, b =>
        {
            b["bankAccount"] = Bank("55556666777788");
            b["notes"] = "Chuyển sang ca đêm";
            b["rowVersion"] = RowVersionOf(saved);
        }));

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var audit = await factory.QueryAsync(db => db.AuditEvents.AsNoTracking().SingleAsync(e => e.RecordId == id && e.Action == "Employee.Updated"));
        Assert.Equal(["BankAccount", "Notes"], JsonDocument.Parse(audit.After!).RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Contains("**********3344", audit.Before);
        Assert.Contains("**********7788", audit.After);
        var allAudit = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == id).Select(e => (e.Before ?? "") + (e.After ?? "")).ToListAsync());
        Assert.DoesNotContain(allAudit, a => a.Contains("11112222333344") || a.Contains("55556666777788"));
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Contains("11112222333344") || l.Contains("55556666777788") || l.Contains(phone));
    }

    [Fact]
    public async Task List_pages_filters_by_status_and_phone_and_shows_the_linked_account()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var tag = NewCode();
        var phone = NewPhone();
        var username = $"nv{NewCode()}";
        var a = IdOf(await CreateAsync(owner, Profile($"{tag} A", phone, zone.Id, b => b["account"] = AccountBlock(username, zone, role))));
        var b = IdOf(await CreateAsync(owner, Profile($"{tag} B", NewPhone(), zone.Id)));
        var c = IdOf(await CreateAsync(owner, Profile($"{tag} C", NewPhone(), null)));
        await EndAsync(owner, b);

        var firstPage = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}&pageSize=2");
        var secondPage = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}&pageSize=2&page=2");
        var defaults = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}");
        var capped = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}&pageSize=500");
        var left = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}&statusId={StatusIds.Employee.Left}");
        var inZone = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}&zoneId={zone.Id}");
        var byPhone = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={phone[3..]}");

        Assert.Equal([a, b], Ids(firstPage));
        Assert.Equal(3, firstPage.GetProperty("total").GetInt32());
        Assert.Equal([c], Ids(secondPage));
        Assert.Equal((1, 20), (defaults.GetProperty("page").GetInt32(), defaults.GetProperty("pageSize").GetInt32()));
        Assert.Equal(100, capped.GetProperty("pageSize").GetInt32());
        Assert.Equal([b], Ids(left));
        Assert.Equal("Left", left.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Equal([a, b], Ids(inZone));
        var row = byPhone.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == a);
        Assert.Equal((phone, username, "Active", "Employed"), (row.GetProperty("phoneNumber").GetString(),
            row.GetProperty("account").GetProperty("username").GetString(), row.GetProperty("account").GetProperty("status").GetString(), row.GetProperty("status").GetString()));
        Assert.Equal(zone.Code, row.GetProperty("workingZones")[0].GetProperty("code").GetString());
    }

    private static async Task<JsonElement> EndAsync(HttpClient owner, Guid id, string endDate = "2026-10-22")
    {
        var response = await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { endDate });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string? StatusIn(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.GetProperty("Status").GetString();

    private static List<Guid> Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
}
