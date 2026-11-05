using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Employees.EmployeesV2QaKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>QA of OMTS-227, who changes what: LABOR-01.1 to 01.4 and BR-LABOR-01 for mixed pairs, All zones and the zones outside the caller's.</summary>
public class EmployeesV2QaMaintainTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private Task<List<Guid>> ZonesAsync(Guid id) =>
        factory.QueryAsync(db => db.EmployeeWorkingZones.Where(w => w.EmployeeId == id).Select(w => w.ZoneId).OrderBy(z => z).ToListAsync());

    [Fact]
    public async Task An_accountant_pair_gives_no_right_to_maintain_in_its_zone()
    {
        var (owner, zoneA, _) = await SetupAsync(factory);
        var zoneB = await factory.AddZoneAsync(NewCode());
        var (name, phone) = ($"Mixed {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, FullProfile(name, phone, zoneA));
        var id = IdOf(saved);
        var version = RowVersionOf(saved);
        var mixed = (await StaffAsync(factory, (StaffRole.ZoneManager, zoneB), (StaffRole.Accountant, null))).Client;

        Assert.Equal("MSG-GEN-08", await (await mixed.PutAsJsonAsync($"/api/employees/{id}", FullProfile(name, phone, zoneA).With("rowVersion", version))).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await mixed.PostAsJsonAsync($"/api/employees/{id}/working-zones", new { zoneId = zoneA.Id })).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await mixed.PostEndAsync(id, new { lastWorkingDay = Today().ToString("yyyy-MM-dd"), rowVersion = version })).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await mixed.PostAsJsonAsync("/api/employees", ApprovedProfile($"New {NewCode()}", NewPhone(), zoneA.Id))).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Created, (await mixed.PostAsJsonAsync("/api/employees", ApprovedProfile($"New {NewCode()}", NewPhone(), zoneB.Id))).StatusCode);
    }

    // LABOR-01.1 step 2: the zones outside theirs stay, whatever is sent; the manager's own zone can go.
    [Fact]
    public async Task A_manager_cannot_remove_or_hide_the_zone_of_another_manager()
    {
        var (owner, zoneA, _) = await SetupAsync(factory);
        var zoneB = await factory.AddZoneAsync(NewCode());
        var (name, phone) = ($"Others {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, FullProfile(name, phone, zoneA, zoneB));
        var id = IdOf(saved);
        var managerA = (await StaffAsync(factory, (StaffRole.ZoneManager, zoneA))).Client;

        var kept = await (await managerA.PutAsJsonAsync($"/api/employees/{id}", FullProfile(name, phone, zoneA).With("rowVersion", RowVersionOf(saved)))).JsonAsync(HttpStatusCode.OK);

        Assert.Equal(2, kept.GetProperty("employee").GetProperty("workingZones").GetArrayLength());
        Assert.Equal(new[] { zoneA.Id, zoneB.Id }.Order(), await ZonesAsync(id));
        var dropped = await (await managerA.PutAsJsonAsync($"/api/employees/{id}", FullProfile(name, phone, zoneB).With("rowVersion", RowVersionOf(kept)))).JsonAsync(HttpStatusCode.OK);

        Assert.Equal([zoneB.Id], await ZonesAsync(id));
        Assert.False(dropped.GetProperty("employee").GetProperty("canMaintain").GetBoolean());
        AssertPersonalFields(dropped.GetProperty("employee"), visible: false);
        var (managerB, _) = await StaffAsync(factory, (StaffRole.ZoneManager, zoneB));
        Assert.True((await (await managerB.GetAsync($"/api/employees/{id}")).JsonAsync(HttpStatusCode.OK)).GetProperty("canMaintain").GetBoolean());
    }

    [Fact]
    public async Task An_all_zones_manager_changes_any_zone_but_never_creates_an_account()
    {
        var (owner, zoneA, role) = await SetupAsync(factory);
        var zoneB = await factory.AddZoneAsync(NewCode());
        var (name, phone) = ($"Everywhere {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, FullProfile(name, phone, zoneA));
        var id = IdOf(saved);
        var everywhere = (await StaffAsync(factory, (StaffRole.ZoneManager, null))).Client;

        var moved = await (await everywhere.PutAsJsonAsync($"/api/employees/{id}", FullProfile(name, phone, zoneB).With("rowVersion", RowVersionOf(saved)))).JsonAsync(HttpStatusCode.OK);

        Assert.Equal([zoneB.Id], await ZonesAsync(id));
        Assert.True(moved.GetProperty("employee").GetProperty("canMaintain").GetBoolean());
        Assert.Equal("MSG-GEN-08", await (await everywhere.PostAsJsonAsync($"/api/employees/{id}/account", AccountBlock($"nv{NewCode()}", zoneB, role))).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await everywhere.PostAsJsonAsync("/api/employees", ApprovedProfile($"New {NewCode()}", NewPhone(), zoneB.Id).With("account", new { username = $"nv{NewCode()}" }))).ErrorCodeAsync());
    }

    // GBR-01, BR-FARM-08: the pair of a Discontinued zone stays Effective; the zone cannot be given anew, and stays on a profile.
    [Fact]
    public async Task A_discontinued_zone_stays_on_a_profile_and_cannot_be_given_anew()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (name, phone) = ($"Closed {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, FullProfile(name, phone, zone));
        var id = IdOf(saved);
        var (manager, _) = await StaffAsync(factory, (StaffRole.ZoneManager, zone));
        await factory.UpdateAsync<FarmingZone>(zone.Id, z => z.StatusId = StatusIds.Usage.Discontinued);

        var view = await (await manager.GetAsync($"/api/employees/{id}")).JsonAsync(HttpStatusCode.OK);
        Assert.True(view.GetProperty("canMaintain").GetBoolean());
        await (await manager.PutAsJsonAsync($"/api/employees/{id}", FullProfile(name, phone, zone).With("notes", "Vẫn sửa được").With("rowVersion", RowVersionOf(saved)))).JsonAsync(HttpStatusCode.OK);

        var added = await manager.PostAsJsonAsync("/api/employees", ApprovedProfile($"New {NewCode()}", NewPhone(), zone.Id));
        Assert.Equal(("MSG-GEN-01", "Working zones"), (await added.ErrorCodeAsync(), await FieldAsync(added)));
    }

    // PM call, recorded: the SRS has no rule against a Zone Manager changing the profile tied to their own account.
    [Fact]
    public async Task A_manager_may_edit_and_end_their_own_profile_and_the_account_stays_as_it_is()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var phone = NewPhone();
        var username = $"nv{NewCode()}";
        var name = $"Self {NewCode()}";
        var saved = await CreateAsync(owner, FullProfile(name, phone, zone).With("account", AccountBlock(username, zone, await factory.RoleAsync(StaffRole.ZoneManager))));
        var id = IdOf(saved);
        var account = await factory.QueryAsync(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Username == username));
        var password = NewPassword();
        await factory.UpdateAsync<Account>(account.Id, a => { a.PasswordHash = Hash(password); a.MustChangePassword = false; a.TemporaryPasswordIssuedAt = null; a.PhoneVerifiedAt = DateTimeOffset.UtcNow; });
        var self = await factory.LoginAsync(username, password);

        var edited = await (await self.PutAsJsonAsync($"/api/employees/{id}", FullProfile(name, phone, zone).With("notes", "Tự sửa").With("rowVersion", RowVersionOf(saved)))).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("Tự sửa", edited.GetProperty("employee").GetProperty("notes").GetString());
        Assert.Equal(JsonValueKind.Null, edited.GetProperty("employee").GetProperty("account").ValueKind);

        await (await self.PostEndAsync(id, new { lastWorkingDay = Today().ToString("yyyy-MM-dd"), rowVersion = RowVersionOf(edited) })).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.Account.Active, await factory.QueryAsync(db => db.Accounts.Where(a => a.Id == account.Id).Select(a => a.StatusId).SingleAsync()));
    }

    // GFR-02: "A refused action shows MSG-GEN-08". The rights are checked after the fields on these two routes.
    [Fact]
    public async Task L6_a_manager_of_another_zone_gets_msg_gen_08_whatever_they_send_on_end_and_resume()
    {
        var (owner, zoneA, _) = await SetupAsync(factory);
        var zoneB = await factory.AddZoneAsync(NewCode());
        var employed = IdOf(await CreateAsync(owner, Profile($"Order {NewCode()}", NewPhone(), zoneA.Id)));
        var left = IdOf(await CreateAsync(owner, Profile($"Order {NewCode()}", NewPhone(), zoneA.Id)));
        await EndAsync(owner, left);
        var other = (await StaffAsync(factory, (StaffRole.ZoneManager, zoneB))).Client;

        var futureDate = await other.PostEndAsync(employed, await EndBodyAsync(owner, employed, Today().AddDays(1)));
        var badPhone = await other.PostAsJsonAsync($"/api/employees/{left}/resume", new { rowVersion = await CurrentVersionAsync(owner, left), phoneNumber = "123" });

        Assert.Equal("MSG-GEN-08", await futureDate.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await badPhone.ErrorCodeAsync());
    }

    [Fact]
    public async Task L6_an_unknown_employee_answers_404_whatever_is_sent_on_end_and_resume()
    {
        var (owner, _, _) = await SetupAsync(factory);
        var unknown = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostEndAsync(unknown, new { lastWorkingDay = Today().AddDays(1).ToString("yyyy-MM-dd"), rowVersion = "AAAA" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/employees/{unknown}/resume", new { rowVersion = "AAAA", phoneNumber = "123" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/employees/{unknown}/working-zones", new { zoneId = Guid.NewGuid() })).StatusCode);
    }

    private string Hash(string password) =>
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<OMTS.Application.Common.Interfaces.Identity.IPasswordHasher>(factory.Services).Hash(password);
}

internal static class ProfileExtensions
{
    public static Dictionary<string, object?> With(this Dictionary<string, object?> body, string key, object? value)
    {
        body[key] = value;
        return body;
    }
}
