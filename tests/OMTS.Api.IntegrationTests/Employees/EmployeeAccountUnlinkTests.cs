using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>
/// LAB-01 account link follow-up (OMTS-214): an edit without <c>accountId</c> keeps the link, and
/// <c>DELETE /api/employees/{id}/account</c> unlinks it (Owner only, audited, harmless when repeated).
/// </summary>
public class EmployeeAccountUnlinkTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Edit_without_account_id_keeps_the_link_and_a_new_account_id_still_relinks()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (first, second) = (await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff), await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff));
        var (name, phone) = ($"Link {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id, b => b["accountId"] = first.Id));
        var id = IdOf(saved);

        // Each edit sends the version the previous one returned, as the screen would.
        async Task<JsonElement> EditAsync(JsonElement read, Action<Dictionary<string, object?>>? change = null) =>
            await (await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id, b =>
            {
                b["notes"] = "Edited";
                b["rowVersion"] = RowVersionOf(read);
                change?.Invoke(b);
            }))).ReadAsync(HttpStatusCode.OK);

        var missing = await EditAsync(saved);
        var explicitNull = await EditAsync(missing, b => b["accountId"] = null);
        var relinked = await EditAsync(explicitNull, b => b["accountId"] = second.Id);

        Assert.Equal(first.Id, AccountOf(missing)?.GetProperty("id").GetGuid());
        Assert.Equal(first.Id, AccountOf(explicitNull)?.GetProperty("id").GetGuid());
        Assert.Equal(second.Id, AccountOf(relinked)?.GetProperty("id").GetGuid());
        Assert.Equal(second.Id, await factory.QueryAsync(db => db.Employees.Where(e => e.Id == id).Select(e => e.AccountId).SingleAsync()));
        // The notes edit, then the new link; the edits without accountId never touched the link.
        var updates = await UpdatesAsync(id);
        Assert.Equal(2, updates.Count);
        Assert.Equal("{\"Notes\":\"Edited\"}", updates[0].After);
        Assert.Equal(($"{{\"AccountId\":\"{first.Id}\"}}", $"{{\"AccountId\":\"{second.Id}\"}}"), (updates[1].Before, updates[1].After));
    }

    [Fact]
    public async Task Delete_unlinks_once_with_one_audit_row_and_a_repeat_changes_nothing()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(staff, role, zone);
        var id = IdOf(await CreateAsync(owner, Profile($"Unlink {NewCode()}", NewPhone(), zone.Id, b => b["accountId"] = staff.Id)));

        var unlinked = await (await owner.DeleteAsync($"/api/employees/{id}/account")).ReadAsync(HttpStatusCode.OK);
        var again = await (await owner.DeleteAsync($"/api/employees/{id}/account")).ReadAsync(HttpStatusCode.OK);

        foreach (var body in new[] { unlinked, again })
        {
            Assert.Equal(JsonValueKind.Null, body.GetProperty("employee").GetProperty("account").ValueKind);
            Assert.Equal("Employee profile saved.", body.GetProperty("message").GetString());
        }

        Assert.Null(await factory.QueryAsync(db => db.Employees.Where(e => e.Id == id).Select(e => e.AccountId).SingleAsync()));
        var update = Assert.Single(await UpdatesAsync(id));
        Assert.Equal(($"{{\"AccountId\":\"{staff.Id}\"}}", "{\"AccountId\":null}"), (update.Before, update.After));
        // The account itself, its grant and its login stay; it can be linked again.
        Assert.Equal(StatusIds.Account.Active, await factory.QueryAsync(db => db.Accounts.Where(a => a.Id == staff.Id).Select(a => a.StatusId).SingleAsync()));
        Assert.Equal(1, await factory.QueryAsync(db => db.AccessGrants.CountAsync(g => g.AccountId == staff.Id && g.StatusId == StatusIds.AccessGrant.Effective)));
        Assert.Contains((await owner.GetFromJsonAsync<JsonElement>("/api/employees/linkable-accounts")).EnumerateArray(), a => a.GetProperty("id").GetGuid() == staff.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/employees/{Guid.NewGuid()}/account")).StatusCode);
    }

    [Fact]
    public async Task Only_the_owner_can_unlink()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var id = IdOf(await CreateAsync(owner, Profile($"Guarded {NewCode()}", NewPhone(), zone.Id, b => b["accountId"] = staff.Id)));
        var manager = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(manager, await factory.AddRoleAsync($"ZM {NewCode()}",
            (PermissionFunctions.LaborWorkers, true, true, true), (PermissionFunctions.Attendance, true, true, true)), null);
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);

        foreach (var client in new[] { await factory.LoginAsync(manager.Username), await factory.LoginAsync(trader.Username) })
        {
            Assert.Equal("MSG-GEN-08", await (await client.DeleteAsync($"/api/employees/{id}/account")).ErrorCodeAsync());
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().DeleteAsync($"/api/employees/{id}/account")).StatusCode);
        Assert.Equal(staff.Id, await factory.QueryAsync(db => db.Employees.Where(e => e.Id == id).Select(e => e.AccountId).SingleAsync()));
        Assert.Empty(await UpdatesAsync(id));
    }

    // Real row versions: a stale one is MSG06 and keeps the link; the current one unlinks and issues a new version.
    [SqlServerFact]
    public async Task Unlink_checks_the_row_version_it_is_given()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var staff = await api.AddAccountAsync($"nv{NewCode()}", Password, SubjectType.Staff);
        var (name, phone) = ($"Phạm Thị {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id, b => b["accountId"] = staff.Id));
        var id = IdOf(saved);
        var edited = await (await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id, b => { b["notes"] = "Ghi chú"; b["rowVersion"] = RowVersionOf(saved); })))
            .ReadAsync(HttpStatusCode.OK);

        var stale = await owner.DeleteAsync($"/api/employees/{id}/account?rowVersion={Uri.EscapeDataString(RowVersionOf(saved))}");
        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
        Assert.Equal(staff.Id, await QueryAsync(api, db => db.Employees.Where(e => e.Id == id).Select(e => e.AccountId).SingleAsync()));

        var unlinked = await (await owner.DeleteAsync($"/api/employees/{id}/account?rowVersion={Uri.EscapeDataString(RowVersionOf(edited))}")).ReadAsync(HttpStatusCode.OK);
        Assert.NotEqual(RowVersionOf(edited), RowVersionOf(unlinked));
        Assert.Null(await QueryAsync(api, db => db.Employees.Where(e => e.Id == id).Select(e => e.AccountId).SingleAsync()));
        // The new version is the one to edit with next; the edit keeps the profile unlinked.
        await (await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id, b => b["rowVersion"] = RowVersionOf(unlinked)))).ReadAsync(HttpStatusCode.OK);
        Assert.Null(await QueryAsync(api, db => db.Employees.Where(e => e.Id == id).Select(e => e.AccountId).SingleAsync()));
    }

    private static JsonElement? AccountOf(JsonElement saved) =>
        saved.GetProperty("employee").GetProperty("account") is { ValueKind: JsonValueKind.Object } account ? account : null;

    private Task<List<AuditEvent>> UpdatesAsync(Guid employeeId) => factory.QueryAsync(db => db.AuditEvents.AsNoTracking()
        .Where(e => e.RecordId == employeeId && e.Action == "Employee.Updated").OrderBy(e => e.OccurredAt).ToListAsync());
}
