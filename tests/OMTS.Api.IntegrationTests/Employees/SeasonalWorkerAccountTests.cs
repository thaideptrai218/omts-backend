using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Labor;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>
/// Owner decisions of 25 and 26/11/2026: a seasonal worker has no fixed zone (every caller who may assign work sees them) and NO login account (the 25/11 automatic
/// account with a fixed password was withdrawn). A Staff employee keeps fixed zones and a random one-time password when the Owner issues an account.
/// </summary>
public class SeasonalWorkerAccountTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task A_seasonal_worker_is_saved_without_a_zone_and_without_a_login_account_even_with_a_phone()
    {
        var (owner, _, _) = await SetupAsync(factory);
        var phone = NewPhone();

        var saved = await CreateAsync(owner, SeasonalProfile($"Seasonal {NewCode()}", phone));

        var employee = saved.GetProperty("employee");
        Assert.Empty(employee.GetProperty("workingZones").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, employee.GetProperty("account").ValueKind);
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("temporaryPassword").ValueKind);
        Assert.False(saved.TryGetProperty("fixedPassword", out _));
        Assert.Equal(0, await factory.QueryAsync(db => db.Accounts.CountAsync(a => a.PhoneNumber == phone)));
    }

    [Fact]
    public async Task A_seasonal_worker_without_a_phone_is_saved_and_editing_with_a_phone_still_creates_no_account()
    {
        var (owner, _, _) = await SetupAsync(factory);
        var name = $"NoPhone {NewCode()}";
        var phone = NewPhone();

        var saved = await CreateAsync(owner, SeasonalProfile(name, null));
        var edited = await owner.PutAsJsonAsync($"/api/employees/{IdOf(saved)}", SeasonalProfile(name, phone, null, b => b["rowVersion"] = RowVersionOf(saved)));
        var body = await edited.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("employee").GetProperty("account").ValueKind);
        Assert.Equal(phone, body.GetProperty("employee").GetProperty("phoneNumber").GetString());
        Assert.Equal(0, await factory.QueryAsync(db => db.Accounts.CountAsync(a => a.PhoneNumber == phone)));
    }

    [Fact]
    public async Task A_seasonal_worker_cannot_be_created_with_an_account_block_nor_get_an_account_issued_later()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var seasonal = await CreateAsync(owner, SeasonalProfile($"No login {NewCode()}", NewPhone()));
        var username = $"tv{NewCode()}";

        var withBlock = await owner.PostAsJsonAsync("/api/employees", SeasonalProfile($"Block {NewCode()}", NewPhone(), null, b => b["account"] = AccountBlock(username, zone, role)));
        var issued = await owner.PostAsJsonAsync($"/api/employees/{IdOf(seasonal)}/account", AccountBlock(username, zone, role));

        Assert.Equal(HttpStatusCode.BadRequest, withBlock.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, issued.StatusCode);
        Assert.Equal(0, await factory.QueryAsync(db => db.Accounts.CountAsync(a => a.Username == username)));
    }

    [Fact]
    public async Task No_path_binds_an_account_to_a_seasonal_worker_but_staff_still_links()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var seasonal = await CreateAsync(owner, SeasonalProfile($"No bind {NewCode()}", NewPhone()));
        var staff = await CreateAsync(owner, StaffProfile($"Bind ok {NewCode()}", NewPhone(), zone.Id));
        var free = await owner.PostAsJsonAsync("/api/accounts", new { displayName = "Free", username = $"s{NewCode()}", phoneNumber = NewPhone(), grants = new[] { new { zoneId = zone.Id, roleId = role.Id } } });
        var freeId = (await free.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("account").GetProperty("id").GetGuid();

        var link = await owner.PostAsJsonAsync($"/api/employees/{IdOf(seasonal)}/account/link", new { accountId = freeId });
        var create = await owner.PostAsJsonAsync("/api/accounts", new { displayName = "Seasonal", username = $"s{NewCode()}", phoneNumber = NewPhone(), employeeId = IdOf(seasonal) });
        var linkStaff = await owner.PostAsJsonAsync($"/api/employees/{IdOf(staff)}/account/link", new { accountId = freeId });

        Assert.Equal(HttpStatusCode.BadRequest, link.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal("Account", (await link.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        Assert.Equal("Account", (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        Assert.Equal(HttpStatusCode.OK, linkStaff.StatusCode);
        Assert.Null((await factory.QueryAsync(db => db.Employees.AsNoTracking().SingleAsync(e => e.Id == IdOf(seasonal)))).AccountId);
    }

    [Fact]
    public async Task A_staff_employee_still_needs_a_zone_and_gets_no_account_unless_the_owner_issues_one()
    {
        var (owner, zone, _) = await SetupAsync(factory);

        var refused = await owner.PostAsJsonAsync("/api/employees", StaffProfile($"Staff {NewCode()}", NewPhone()));
        var saved = await CreateAsync(owner, StaffProfile($"Staff {NewCode()}", NewPhone(), zone.Id));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("employee").GetProperty("account").ValueKind);
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("temporaryPassword").ValueKind);
    }

    [Fact]
    public async Task A_zone_manager_creates_a_seasonal_worker_with_no_zone_and_no_account()
    {
        var (_, zoneA, _) = await SetupAsync(factory);
        var manager = await ManagerAsync(factory, zoneA);

        var saved = await CreateAsync(manager, SeasonalProfile($"Called {NewCode()}", NewPhone(), null, b => b["ownerApproved"] = true));

        Assert.Equal(JsonValueKind.Null, saved.GetProperty("temporaryPassword").ValueKind);
        Assert.Empty(saved.GetProperty("employee").GetProperty("workingZones").EnumerateArray());
        Assert.True(saved.GetProperty("employee").GetProperty("canMaintain").GetBoolean());
    }

    [Fact]
    public async Task Every_manager_who_may_assign_work_sees_and_edits_a_seasonal_worker_but_a_staff_employee_only_in_their_zones()
    {
        var (owner, zoneA, _) = await SetupAsync(factory);
        var zoneB = await factory.AddZoneAsync(NewCode());
        var tag = NewCode();
        var seasonal = await CreateAsync(owner, SeasonalProfile($"{tag} Seasonal", NewPhone()));
        var staff = await CreateAsync(owner, StaffProfile($"{tag} Staff", NewPhone(), zoneA.Id));
        var (inA, inB, accountant) = (await ManagerAsync(factory, zoneA), await ManagerAsync(factory, zoneB), await ManagerAsync(factory, zoneB, StaffRole.Accountant));

        foreach (var client in new[] { inA, inB })
        {
            var list = await client.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}&pageSize=50");
            var names = list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("fullName").GetString()).Order().ToList();
            Assert.Contains($"{tag} Seasonal", names);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/employees/{IdOf(seasonal)}")).StatusCode);
        }

        Assert.Contains($"{tag} Staff", (await inA.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("fullName").GetString()));
        Assert.DoesNotContain($"{tag} Staff", (await inB.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("fullName").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await inB.GetAsync($"/api/employees/{IdOf(staff)}")).StatusCode);

        // A manager of another zone edits the seasonal worker (the type and zones stay), but an Accountant only reads.
        var edit = await inB.PutAsJsonAsync($"/api/employees/{IdOf(seasonal)}", SeasonalProfile($"{tag} Seasonal", seasonal.GetProperty("employee").GetProperty("phoneNumber").GetString(), null, b => { b["notes"] = "Gọi thứ hai"; b["rowVersion"] = RowVersionOf(seasonal); }));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var seen = await accountant.GetFromJsonAsync<JsonElement>($"/api/employees/{IdOf(seasonal)}");
        Assert.False(seen.GetProperty("canMaintain").GetBoolean());

    }

    [Fact]
    public async Task The_issue_account_route_keeps_the_random_one_time_password_of_a_staff_employee()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var staff = await CreateAsync(owner, StaffProfile($"Later staff {NewCode()}", NewPhone(), zone.Id));

        var staffAccount = await owner.PostAsJsonAsync($"/api/employees/{IdOf(staff)}/account", AccountBlock($"nv{NewCode()}", zone, role));
        var created = await staffAccount.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, staffAccount.StatusCode);
        Assert.Equal(10, created.GetProperty("temporaryPassword").GetString()!.Length);
        Assert.False(created.TryGetProperty("fixedPassword", out _));
        Assert.NotNull(created.GetProperty("temporaryPasswordExpiresAt").GetString());
    }

    [Fact]
    public async Task Assigning_a_seasonal_worker_in_any_zone_does_not_give_them_a_working_zone()
    {
        var s = await WorkTestKit.SetupWorkAsync(factory);
        var created = await CreateAsync(s.Owner, SeasonalProfile($"Roaming {NewCode()}", NewPhone()));
        var id = IdOf(created);

        // The manager of the zone sees and assigns them, then the Owner assigns them in the other zone: neither adds a working zone.
        var assigned = await WorkTestKit.AssignAsync(s.Manager, s.Raft.Id, s.Harvest, Today(), false, id);
        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
        var moved = await WorkTestKit.AssignAsync(s.Owner, s.OtherRaft.Id, s.Harvest, Today(), true, id);
        Assert.Equal(HttpStatusCode.Created, moved.StatusCode);

        Assert.Empty(await factory.QueryAsync(db => db.EmployeeWorkingZones.AsNoTracking().Where(w => w.EmployeeId == id).ToListAsync()));
    }
}
