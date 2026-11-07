using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>
/// LABOR-01 access per the §I.4.4 matrix (Owner Full, Zone Manager Full*, others No) and BR-LABOR-01: every Zone Manager sees
/// every employee and maintains those with a working zone among their zones; an All zones pair maintains everyone.
/// </summary>
public class EmployeeZoneScopeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Accountants_traders_and_anonymous_callers_are_refused_on_every_endpoint()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var saved = await CreateAsync(owner, Profile($"Access {NewCode()}", NewPhone(), zone.Id));
        var id = IdOf(saved);
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var body = Profile($"Intruder {NewCode()}", NewPhone(), zone.Id, b => { b["ownerApproved"] = true; b["rowVersion"] = RowVersionOf(saved); });
        var calls = new Func<HttpClient, Task<HttpResponseMessage>>[]
        {
            c => c.GetAsync("/api/employees"),
            c => c.GetAsync($"/api/employees/{id}"),
            c => c.PostAsJsonAsync("/api/employees", body),
            c => c.PutAsJsonAsync($"/api/employees/{id}", body),
            c => c.PostAsJsonAsync($"/api/employees/{id}/working-zones", new { zoneId = zone.Id }),
            c => c.PostAsJsonAsync($"/api/employees/{id}/account", AccountBlock($"nv{NewCode()}", zone, role)),
            c => c.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { lastWorkingDay = "2026-10-01", rowVersion = RowVersionOf(saved) }),
            c => c.PostAsJsonAsync($"/api/employees/{id}/resume", new { rowVersion = RowVersionOf(saved) }),
        };

        foreach (var client in new[]
        {
            await ManagerAsync(factory, zone, StaffRole.Accountant),
            await ManagerAsync(factory, null, StaffRole.Accountant),
            await factory.LoginAsync(trader.Username),
        })
        {
            foreach (var call in calls)
            {
                Assert.Equal("MSG-GEN-08", await (await call(client)).ErrorCodeAsync());
            }
        }

        foreach (var call in calls)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await call(factory.CreateClient())).StatusCode);
        }

        var stored = await factory.QueryAsync(db => db.Employees.AsNoTracking().SingleAsync(e => e.Id == id));
        Assert.Equal((StatusIds.Employee.Working, (Guid?)null), (stored.StatusId, stored.AccountId));
        Assert.False(await factory.QueryAsync(db => db.Employees.AnyAsync(e => e.FullName == (string)body["fullName"]!)));
    }

    [Fact]
    public async Task A_manager_of_another_zone_sees_the_employee_without_personal_data_and_cannot_change_them()
    {
        var (owner, zoneA, _) = await SetupAsync(factory);
        var zoneB = await factory.AddZoneAsync(NewCode());
        var saved = await CreateAsync(owner, Profile($"Scope {NewCode()}", NewPhone(), zoneA.Id, b => { b["bankAccount"] = Bank(); b["notes"] = "Riêng tư"; }));
        var id = IdOf(saved);
        var (inZone, otherZone, everywhere) = (await ManagerAsync(factory, zoneA), await ManagerAsync(factory, zoneB), await ManagerAsync(factory, null));

        foreach (var (client, maintains) in new[] { (inZone, true), (otherZone, false), (everywhere, true) })
        {
            var view = await client.GetFromJsonAsync<JsonElement>($"/api/employees/{id}");
            Assert.Equal(saved.GetProperty("employee").GetProperty("phoneNumber").GetString(), view.GetProperty("phoneNumber").GetString());
            Assert.Equal(zoneA.Code, view.GetProperty("workingZones")[0].GetProperty("code").GetString());
            Assert.Equal(maintains, view.GetProperty("canMaintain").GetBoolean());
            Assert.Equal(maintains ? JsonValueKind.Object : JsonValueKind.Null, view.GetProperty("bankAccount").ValueKind);
            Assert.Equal(maintains ? JsonValueKind.String : JsonValueKind.Null, view.GetProperty("notes").ValueKind);
            Assert.Equal(JsonValueKind.Null, view.GetProperty("account").ValueKind);
        }

        var body = Profile("Bị đổi", null, zoneB.Id, b => b["rowVersion"] = RowVersionOf(saved));
        foreach (var refused in new[]
        {
            await otherZone.PutAsJsonAsync($"/api/employees/{id}", body),
            await otherZone.PostAsJsonAsync($"/api/employees/{id}/end-employment", await EndBodyAsync(owner, id, Today())),
            await otherZone.PostAsJsonAsync($"/api/employees/{id}/resume", new { rowVersion = RowVersionOf(saved) }),
        })
        {
            Assert.Equal("MSG-GEN-08", await refused.ErrorCodeAsync());
        }

        Assert.Equal(StatusIds.Employee.Working, await factory.QueryAsync(db => db.Employees.Where(e => e.Id == id).Select(e => e.StatusId).SingleAsync()));
    }

    [Fact]
    public async Task A_manager_adds_only_in_their_own_zones_and_all_zones_adds_in_any()
    {
        var (_, zoneA, _) = await SetupAsync(factory);
        var zoneB = await factory.AddZoneAsync(NewCode());
        var (manager, everywhere) = (await ManagerAsync(factory, zoneA), await ManagerAsync(factory, null));
        var refusedName = $"Other zone {NewCode()}";

        var own = await CreateAsync(manager, ApprovedProfile($"Own {NewCode()}", NewPhone(), zoneA.Id));
        var outside = await manager.PostAsJsonAsync("/api/employees", ApprovedProfile(refusedName, NewPhone(), zoneB.Id));
        var both = await manager.PostAsJsonAsync("/api/employees", Profile(refusedName, NewPhone(), zoneA.Id, b => { b["ownerApproved"] = true; b["workingZoneIds"] = new[] { zoneA.Id, zoneB.Id }; }));
        var any = await CreateAsync(everywhere, ApprovedProfile($"Any {NewCode()}", NewPhone(), zoneB.Id));

        Assert.True(own.GetProperty("employee").GetProperty("canMaintain").GetBoolean());
        Assert.Equal("MSG-GEN-08", await outside.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await both.ErrorCodeAsync());
        Assert.True(any.GetProperty("employee").GetProperty("canMaintain").GetBoolean());
        Assert.False(await factory.QueryAsync(db => db.Employees.AnyAsync(e => e.FullName == refusedName)));
    }

    // LABOR-01.1 step 2: "the working zones that are not among their own zones stay as they are".
    [Fact]
    public async Task An_edit_keeps_the_zones_outside_the_managers_and_gives_only_their_own()
    {
        var (owner, zoneA, _) = await SetupAsync(factory);
        var (zoneB, zoneC) = (await factory.AddZoneAsync(NewCode()), await factory.AddZoneAsync(NewCode()));
        var (name, phone) = ($"Edit {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, Profile(name, phone, zoneA.Id, b => b["workingZoneIds"] = new[] { zoneA.Id, zoneB.Id }));
        var id = IdOf(saved);
        var manager = await ManagerAsync(factory, zoneA);

        var tooFar = await manager.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zoneC.Id, b => b["rowVersion"] = RowVersionOf(saved)));
        Assert.Equal("MSG-GEN-08", await tooFar.ErrorCodeAsync());

        // The form may send back the zone of the other manager or leave it out: it stays either way.
        var withOther = await (await manager.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zoneB.Id, b => b["rowVersion"] = RowVersionOf(saved)))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal([zoneB.Id], await ZonesAsync(id));
        Assert.False(withOther.GetProperty("employee").GetProperty("canMaintain").GetBoolean());

        var restored = await (await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username))
            .PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zoneA.Id, b => { b["workingZoneIds"] = new[] { zoneA.Id, zoneB.Id }; b["rowVersion"] = RowVersionOf(withOther); }));
        var current = await restored.Content.ReadFromJsonAsync<JsonElement>();
        var dropsOwn = await manager.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, null, b => b["rowVersion"] = RowVersionOf(current)));

        Assert.Equal(HttpStatusCode.OK, dropsOwn.StatusCode);
        Assert.Equal([zoneB.Id], await ZonesAsync(id));
    }

    [Fact]
    public async Task A_manager_cannot_leave_an_employee_without_a_working_zone()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (name, phone) = ($"Last zone {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id));

        var refused = await (await ManagerAsync(factory, zone)).PutAsJsonAsync($"/api/employees/{IdOf(saved)}", Profile(name, phone, null, b => b["rowVersion"] = RowVersionOf(saved)));

        Assert.Equal(("MSG-GEN-01", "Working zones"), (await refused.ErrorCodeAsync(), await FieldAsync(refused)));
        Assert.Equal([zone.Id], await ZonesAsync(IdOf(saved)));
    }

    // LABOR-01.2 and BR-LABOR-01: one of their own zones, on any Employed employee.
    [Fact]
    public async Task Add_my_zone_gives_the_managers_zone_to_any_employed_employee_and_then_they_maintain_them()
    {
        var (owner, zoneA, _) = await SetupAsync(factory);
        var (zoneB, closed) = (await factory.AddZoneAsync(NewCode()), await factory.AddZoneAsync(NewCode()));
        await factory.UpdateAsync<FarmingZone>(closed.Id, z => z.StatusId = StatusIds.Usage.Discontinued);
        var saved = await CreateAsync(owner, Profile($"Add zone {NewCode()}", NewPhone(), zoneA.Id));
        var id = IdOf(saved);
        var manager = await ManagerAsync(factory, zoneB);

        var notMine = await manager.PostAsJsonAsync($"/api/employees/{id}/working-zones", new { zoneId = zoneA.Id });
        var missing = await manager.PostAsJsonAsync($"/api/employees/{id}/working-zones", new { });
        var discontinued = await (await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username)).PostAsJsonAsync($"/api/employees/{id}/working-zones", new { zoneId = closed.Id });
        var added = await (await manager.PostAsJsonAsync($"/api/employees/{id}/working-zones", new { zoneId = zoneB.Id })).BodyAsync(HttpStatusCode.OK);
        var again = await (await manager.PostAsJsonAsync($"/api/employees/{id}/working-zones", new { zoneId = zoneB.Id })).BodyAsync(HttpStatusCode.OK);

        Assert.Equal("MSG-GEN-08", await notMine.ErrorCodeAsync());
        Assert.Equal(("MSG-GEN-01", "Working zones"), (await missing.ErrorCodeAsync(), await FieldAsync(missing)));
        Assert.Equal(("MSG-GEN-01", "Working zones"), (await discontinued.ErrorCodeAsync(), await FieldAsync(discontinued)));
        Assert.True(added.GetProperty("employee").GetProperty("canMaintain").GetBoolean());
        Assert.Equal("MSG-GEN-05", added.GetProperty("messageCode").GetString());
        Assert.Equal(new[] { zoneA.Id, zoneB.Id }.Order(), (await ZonesAsync(id)).Order());
        Assert.Equal(RowVersionOf(added), RowVersionOf(again));
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == id && e.Action == "Employee.WorkingZoneAdded" && e.ZoneId == zoneB.Id)));

        await EndAsync(owner, id);
        Assert.Equal("MSG-GEN-09", await (await (await ManagerAsync(factory, null)).PostAsJsonAsync($"/api/employees/{id}/working-zones", new { zoneId = closed.Id })).ErrorCodeAsync());
    }

    // SCR-LABOR-01: "every employee of the cooperative, those of the user's zones first".
    [Fact]
    public async Task The_list_shows_every_employee_with_those_of_the_managers_zones_first()
    {
        var (owner, zoneA, _) = await SetupAsync(factory);
        var zoneB = await factory.AddZoneAsync(NewCode());
        var tag = NewCode();
        var other = IdOf(await CreateAsync(owner, Profile($"{tag} A other", NewPhone(), zoneB.Id)));
        var mine = IdOf(await CreateAsync(owner, Profile($"{tag} B mine", NewPhone(), zoneA.Id)));

        var managerList = await (await ManagerAsync(factory, zoneA)).GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}");
        var ownerList = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}");

        Assert.Equal([mine, other], managerList.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal([true, false], managerList.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("canMaintain").GetBoolean()));
        Assert.Equal([other, mine], ownerList.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.All(ownerList.GetProperty("items").EnumerateArray(), i => Assert.True(i.GetProperty("canMaintain").GetBoolean()));
    }

    private Task<List<Guid>> ZonesAsync(Guid id) =>
        factory.QueryAsync(db => db.EmployeeWorkingZones.Where(w => w.EmployeeId == id).Select(w => w.ZoneId).ToListAsync());
}
