using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Labor;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>
/// Owner decisions of 25/11/2026: a seasonal worker has no fixed zone (every caller who may assign work sees them), and a login account with the fixed initial
/// password that must be changed at the first login; the EMPLOYEE role is the pair that lets it sign in. A Staff employee keeps fixed zones and a random one-time password.
/// </summary>
public class SeasonalWorkerAccountTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string InitialPassword = "OMTS@1234";

    [Fact]
    public async Task A_seasonal_worker_is_saved_without_a_zone_with_a_login_of_the_fixed_password_that_must_be_changed()
    {
        var (owner, _, _) = await SetupAsync(factory);
        var phone = NewPhone();

        var saved = await CreateAsync(owner, SeasonalProfile($"Seasonal {NewCode()}", phone));

        var employee = saved.GetProperty("employee");
        Assert.Empty(employee.GetProperty("workingZones").EnumerateArray());
        Assert.Equal(InitialPassword, saved.GetProperty("temporaryPassword").GetString());
        Assert.True(saved.GetProperty("fixedPassword").GetBoolean());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("temporaryPasswordExpiresAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("accountNote").ValueKind);
        var username = employee.GetProperty("account").GetProperty("username").GetString()!;
        Assert.Equal(employee.GetProperty("code").GetString()!.ToLowerInvariant(), username);

        // One Effective EMPLOYEE pair for All zones, granted by the person who saved the profile.
        var grants = await factory.QueryAsync(db => db.AccessGrants.AsNoTracking().Include(g => g.Role).Where(g => g.Account.Username == username).ToListAsync());
        var pair = Assert.Single(grants);
        Assert.Equal((FixedRoles_EmployeeCode, (Guid?)null, AccessScopeType.AllZones, StatusIds.AccessGrant.Effective), (pair.Role.Code, pair.ZoneId, pair.ScopeType, pair.StatusId));

        // The login works with the fixed password, the phone comes first, then the forced change; the fixed password then stops working.
        var client = factory.CreateClient();
        var tokens = await LoginTokensAsync(client, username, InitialPassword);
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.GetProperty("accessToken").GetString());
        Assert.True(tokens.GetProperty("mustChangePassword").GetBoolean());
        Assert.True(tokens.GetProperty("phoneVerificationRequired").GetBoolean());
        var verification = await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/phone-verification", new { }));
        var token = await VerifyAsync(client, verification, factory.LastSmsCode(phone));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/me/phone-verification/confirm", new { verificationToken = token })).StatusCode);
        const string chosen = "Moi-Matkhau-77";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/me/password", new { newPassword = InitialPassword, passwordConfirmation = InitialPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/me/password", new { newPassword = chosen, passwordConfirmation = chosen })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = username, password = InitialPassword })).StatusCode);
        var signedIn = await factory.LoginAsync(username, chosen);
        var permissions = await signedIn.GetFromJsonAsync<JsonElement>("/api/me/permissions");
        Assert.Equal("EMPLOYEE", Assert.Single(permissions.GetProperty("grants").EnumerateArray()).GetProperty("roleCode").GetString());
        Assert.Empty(permissions.GetProperty("functions").EnumerateArray());
    }

    private const string FixedRoles_EmployeeCode = "EMPLOYEE";

    [Fact]
    public async Task The_fixed_password_does_not_expire_after_a_day()
    {
        var (owner, _, _) = await SetupAsync(factory);
        var saved = await CreateAsync(owner, SeasonalProfile($"Late {NewCode()}", NewPhone()));
        var username = saved.GetProperty("employee").GetProperty("account").GetProperty("username").GetString()!;

        var stored = await factory.QueryAsync(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Username == username));

        // A seasonal worker is called when there is work and may sign in for the first time weeks later.
        Assert.True(stored.MustChangePassword);
        Assert.False(stored.IsTemporaryPasswordExpired(DateTimeOffset.UtcNow.AddDays(30)));
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = username, password = InitialPassword })).StatusCode);
    }

    [Fact]
    public async Task A_staff_employee_still_needs_a_zone_and_gets_no_account_and_no_fixed_password()
    {
        var (owner, zone, _) = await SetupAsync(factory);

        var refused = await owner.PostAsJsonAsync("/api/employees", StaffProfile($"Staff {NewCode()}", NewPhone()));
        var saved = await CreateAsync(owner, StaffProfile($"Staff {NewCode()}", NewPhone(), zone.Id));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("employee").GetProperty("account").ValueKind);
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("temporaryPassword").ValueKind);
        Assert.False(saved.GetProperty("fixedPassword").GetBoolean());
    }

    [Fact]
    public async Task A_seasonal_worker_without_a_phone_has_no_account_until_a_phone_is_added()
    {
        var (owner, _, _) = await SetupAsync(factory);
        var name = $"NoPhone {NewCode()}";

        var saved = await CreateAsync(owner, SeasonalProfile(name, null));
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("employee").GetProperty("account").ValueKind);

        var edited = await owner.PutAsJsonAsync($"/api/employees/{IdOf(saved)}", SeasonalProfile(name, NewPhone(), null, b => b["rowVersion"] = RowVersionOf(saved)));
        var body = await edited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(InitialPassword, body.GetProperty("temporaryPassword").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("employee").GetProperty("account").ValueKind);
    }

    [Fact]
    public async Task A_phone_another_account_holds_saves_the_worker_without_an_account_and_says_why()
    {
        var (owner, _, _) = await SetupAsync(factory);
        var phone = NewPhone();
        await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, a => a.PhoneNumber = phone);

        var saved = await CreateAsync(owner, SeasonalProfile($"Shared {NewCode()}", phone));

        Assert.Equal("MSG-ACCOUNT-09", saved.GetProperty("accountNote").GetString());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("employee").GetProperty("account").ValueKind);
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("temporaryPassword").ValueKind);
    }

    [Fact]
    public async Task A_zone_manager_creates_a_seasonal_worker_with_no_zone_and_the_account_comes_with_it()
    {
        var (_, zoneA, _) = await SetupAsync(factory);
        var manager = await ManagerAsync(factory, zoneA);

        var saved = await CreateAsync(manager, SeasonalProfile($"Called {NewCode()}", NewPhone(), null, b => b["ownerApproved"] = true));

        Assert.Equal(InitialPassword, saved.GetProperty("temporaryPassword").GetString());
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
    public async Task Resetting_a_seasonal_account_gives_the_fixed_password_again_and_a_staff_account_a_random_one()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var seasonal = await CreateAsync(owner, SeasonalProfile($"Reset {NewCode()}", NewPhone()));
        var seasonalAccount = seasonal.GetProperty("employee").GetProperty("account").GetProperty("id").GetGuid();
        var staffAccount = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(staffAccount, StaffRole.ZoneManager, zone);
        await factory.UpdateAsync<Account>(seasonalAccount, a => { a.MustChangePassword = false; a.TemporaryPasswordIssuedAt = null; });

        var seasonalReset = await (await owner.PostAsync($"/api/accounts/{seasonalAccount}/reset-password", null)).Content.ReadFromJsonAsync<JsonElement>();
        var staffReset = await (await owner.PostAsync($"/api/accounts/{staffAccount.Id}/reset-password", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(InitialPassword, seasonalReset.GetProperty("temporaryPassword").GetString());
        Assert.True(seasonalReset.GetProperty("fixedPassword").GetBoolean());
        Assert.Equal(JsonValueKind.Null, seasonalReset.GetProperty("temporaryPasswordExpiresAt").ValueKind);
        Assert.True(seasonalReset.GetProperty("account").GetProperty("mustChangePassword").GetBoolean());
        Assert.NotEqual(InitialPassword, staffReset.GetProperty("temporaryPassword").GetString());
        Assert.False(staffReset.GetProperty("fixedPassword").GetBoolean());
        Assert.Equal(10, staffReset.GetProperty("temporaryPassword").GetString()!.Length);
    }

    [Fact]
    public async Task The_issue_account_route_keeps_the_random_password_of_a_staff_employee_and_refuses_a_seasonal_worker_who_has_an_account()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var seasonal = await CreateAsync(owner, SeasonalProfile($"Has account {NewCode()}", NewPhone()));
        var staff = await CreateAsync(owner, StaffProfile($"Later staff {NewCode()}", NewPhone(), zone.Id));

        var staffAccount = await owner.PostAsJsonAsync($"/api/employees/{IdOf(staff)}/account", AccountBlock($"nv{NewCode()}", zone, role));
        var created = await staffAccount.Content.ReadFromJsonAsync<JsonElement>();
        var seasonalAgain = await owner.PostAsJsonAsync($"/api/employees/{IdOf(seasonal)}/account", new { username = $"tv{NewCode()}" });

        Assert.Equal(HttpStatusCode.Created, staffAccount.StatusCode);
        Assert.Equal(10, created.GetProperty("temporaryPassword").GetString()!.Length);
        Assert.False(created.GetProperty("fixedPassword").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, seasonalAgain.StatusCode);
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

    [Fact]
    public async Task The_seed_step_creates_the_missing_accounts_once_and_skips_a_taken_phone_or_no_phone()
    {
        var (_, _, _) = await SetupAsync(factory);
        var cooperative = await factory.EnsureCooperativeAsync();
        var (free, taken, none, staff) = (NewPhone(), NewPhone(), (string?)null, NewPhone());
        await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, a => a.PhoneNumber = taken);
        var ids = new Dictionary<string, Guid>();
        foreach (var (key, phone, type) in new[] { ("free", (string?)free, EmployeeType.SeasonalWorker), ("taken", taken, EmployeeType.SeasonalWorker), ("none", none, EmployeeType.SeasonalWorker), ("staff", staff, EmployeeType.Staff) })
        {
            var employee = new Employee { CooperativeId = cooperative.Id, FullName = $"Old {key} {NewCode()}", Code = $"OLD{NewCode()[1..7]}".ToUpperInvariant(), PhoneNumber = phone };
            employee.TypePeriods.Add(new EmployeeTypePeriod { Type = type, EffectiveFrom = new DateOnly(2026, 1, 1) });
            ids[key] = employee.Id;
            await factory.UseDbAsync(db => db.Employees.Add(employee));
        }

        var first = await RunBackfillAsync();
        var second = await RunBackfillAsync();

        var after = await factory.QueryAsync(db => db.Employees.AsNoTracking().Where(e => ids.Values.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.AccountId));
        Assert.NotNull(after[ids["free"]]);
        Assert.Null(after[ids["taken"]]);
        Assert.Null(after[ids["none"]]);
        Assert.Null(after[ids["staff"]]);
        var account = await factory.QueryAsync(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Id == after[ids["free"]]));
        Assert.Equal((free, SubjectType.Staff, true), (account.PhoneNumber, account.SubjectType, account.MustChangePassword));
        Assert.Single(await factory.QueryAsync(db => db.AccessGrants.AsNoTracking().Where(g => g.AccountId == account.Id && g.Role.Code == "EMPLOYEE").ToListAsync()));
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = account.Username, password = InitialPassword })).StatusCode);
        Assert.StartsWith("Seasonal accounts:", first);
        // Run again: nothing is added twice.
        Assert.Equal(1, await factory.QueryAsync(db => db.Accounts.CountAsync(a => a.PhoneNumber == free)));
        Assert.StartsWith("Seasonal accounts:", second);
    }

    private async Task<string> RunBackfillAsync()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SeasonalAccountBackfill>().RunAsync(CancellationToken.None);
    }
}
