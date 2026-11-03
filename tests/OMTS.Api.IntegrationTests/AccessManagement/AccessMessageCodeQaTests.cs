using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.AccessManagement;

/// <summary>
/// QA of OMTS-216 PR 0.2 on ACCOUNT-09 and FARM-05 (SRS 27/10): the context splits of old MSG01/MSG03/MSG61 into
/// MSG-ACCOUNT-09, MSG-GEN-01/02/09, with the filled-in texts and the legacy code of each call site.
/// </summary>
public class AccessMessageCodeQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // ACCOUNT-09.0.E1: MSG-ACCOUNT-09 naming the field, with the other account (MSG03 "Username already exists" before).
    [Fact]
    public async Task Staff_account_with_a_taken_username_or_phone_is_msg_account_09_naming_the_field()
    {
        var (owner, zone, role) = await SetupAsync();
        var existing = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);

        var username = await (await owner.PostAsJsonAsync("/api/accounts", StaffBody(zone, role, existing.Username, NewPhone())))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-ACCOUNT-09", "MSG03");
        var phone = await (await owner.PostAsJsonAsync("/api/accounts", StaffBody(zone, role, $"s{NewCode()}", existing.PhoneNumber!)))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-ACCOUNT-09", "MSG03");

        Assert.Equal("This username is already used by another account. Please enter another one.", username.Detail());
        Assert.Equal(("Username", existing.Id), (username.Str("field"), username.GetProperty("existingId").GetGuid()));
        Assert.Equal("This phone number is already used by another account. Please enter another one.", phone.Detail());
        Assert.Equal(("Phone number", existing.Id), (phone.Str("field"), phone.GetProperty("existingId").GetGuid()));
    }

    // FARM-05 step 3 / GBR-01: a discontinued zone is MSG-GEN-09 naming it (MSG01 "Zone is invalid" before); an
    // unknown zone stays MSG-GEN-01. The status stays 400.
    [Fact]
    public async Task Grant_in_a_discontinued_zone_is_msg_gen_09_naming_the_zone_and_an_unknown_zone_msg_gen_01()
    {
        var (owner, _, role) = await SetupAsync();
        var stopped = await factory.AddZoneAsync(NewCode());
        await factory.UpdateAsync<FarmingZone>(stopped.Id, z => z.StatusId = StatusIds.Usage.Discontinued);
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);

        var discontinued = await (await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = stopped.Id, roleId = role.Id }))
            .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-09", "MSG01");
        var unknown = await (await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = Guid.NewGuid(), roleId = role.Id }))
            .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");

        Assert.Equal($"Farming zone {stopped.Code} is Discontinued and cannot be used for this action.", discontinued.Detail());
        Assert.Equal("Zone", discontinued.Str("field"));
        Assert.Equal("Zone is missing or invalid. Please check and enter it again.", unknown.Detail());
    }

    // FARM-05.0.E1 (MSG-GEN-02, MSG03 before) and a revoke of a revoked pair (MSG-GEN-09, MSG61 before).
    [Fact]
    public async Task Duplicate_pair_is_msg_gen_02_and_a_second_revoke_msg_gen_09()
    {
        var (owner, zone, role) = await SetupAsync();
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var grant = await (await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = zone.Id, roleId = role.Id })).BodyAsync(HttpStatusCode.Created);

        var duplicate = await (await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = zone.Id, roleId = role.Id }))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02", "MSG03");
        await (await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants/{grant.GetProperty("id").GetGuid()}/revoke", new { reason = "Moved" })).BodyAsync(HttpStatusCode.OK);
        var again = await (await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants/{grant.GetProperty("id").GetGuid()}/revoke", new { reason = "Again" }))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09", "MSG61");

        Assert.Equal("Zone-role pair already exists. Your entries are kept: change them, or open the existing record.", duplicate.Detail());
        Assert.Equal(grant.GetProperty("id").GetGuid(), duplicate.GetProperty("existingId").GetGuid());
        Assert.Equal("Zone-role pair is Revoked and cannot be used for this action.", again.Detail());
    }

    // ACCOUNT-09.2: a second lock is MSG-GEN-09 (MSG61 "Cannot change Account from … to …" before).
    [Fact]
    public async Task Second_lock_is_msg_gen_09_with_legacy_msg61()
    {
        var (owner, zone, role) = await SetupAsync();
        var created = await (await owner.PostAsJsonAsync("/api/accounts", StaffBody(zone, role, $"s{NewCode()}", NewPhone()))).BodyAsync(HttpStatusCode.Created);
        var id = created.GetProperty("account").GetProperty("id").GetGuid();

        await (await owner.PostAsJsonAsync($"/api/accounts/{id}/lock", new { reason = "Left" })).BodyAsync(HttpStatusCode.OK);
        var again = await (await owner.PostAsJsonAsync($"/api/accounts/{id}/lock", new { reason = "Again" }))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09", "MSG61");

        Assert.Equal("Account is Locked and cannot be used for this action.", again.Detail());
    }

    // Roles live until PR 0.3: the impact carries MSG-GEN-04 (MSG09 before) and a held role MSG-GEN-06 (MSG11 before).
    [Fact]
    public async Task Role_impact_carries_msg_gen_04_and_a_held_role_cannot_be_discontinued_with_msg_gen_06()
    {
        var (owner, zone, role) = await SetupAsync();
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(staff, role, zone);

        var impact = await (await owner.GetAsync($"/api/roles/{role.Id}/impact")).BodyAsync(HttpStatusCode.OK);
        var held = await (await owner.PostAsync($"/api/roles/{role.Id}/discontinue", null)).SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-06", "MSG11");

        Assert.Equal("MSG-GEN-04", impact.Str("confirmMessageCode"));
        Assert.Equal("Are you sure you want to edit this role? This action is recorded in the audit log.", impact.Str("confirmMessage"));
        Assert.StartsWith("Cannot proceed because 1 records are still in use: ", held.Detail());
        Assert.EndsWith(". Please handle these records first.", held.Detail());
    }

    private async Task<(HttpClient Owner, FarmingZone Zone, OMTS.Domain.Entities.Identity.Role Role)> SetupAsync()
    {
        await factory.EnsureCooperativeAsync();
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username);
        var zone = await factory.AddZoneAsync(NewCode());
        var role = await factory.AddRoleAsync($"R {NewCode()}", (PermissionFunctions.WaterMeasurements, true, false, false));
        return (owner, zone, role);
    }

    private static object StaffBody(FarmingZone zone, OMTS.Domain.Entities.Identity.Role role, string username, string phone) => new
    {
        displayName = "Nguyễn Văn QA",
        username,
        phoneNumber = phone,
        grants = new[] { new { zoneId = zone.Id, roleId = role.Id } },
    };
}
