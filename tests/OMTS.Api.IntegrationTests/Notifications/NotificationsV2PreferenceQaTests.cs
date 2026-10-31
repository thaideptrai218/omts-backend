using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Notifications;

/// <summary>
/// QA of OMTS-216 PR 0.5 on ACCOUNT-07 (BR-ACCOUNT-11): push per type only for the types of the roles the account
/// holds now, mandatory types always pushed and never stored, an invalid request changes nothing.
/// </summary>
public class NotificationsV2PreferenceQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // Generated per run, so no password-like literal is committed (secret scanners).
    private static readonly string TestPassword = NewPassword();

    // A role is offered only the types it receives and can open: the license reminder is Owner only (GFR-03), the
    // Accountant and traders see no water record (§I.4.4), the lock reminder is never pushed (BR-LABOR-04).
    [Fact]
    public async Task A_type_not_offered_to_the_roles_held_is_refused_and_nothing_is_stored()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var manager = await StaffAsync(StaffRole.ZoneManager, zone);
        var accountant = await StaffAsync(StaffRole.Accountant, null);
        var trader = await AccountAsync(SubjectType.Trader);

        var attempts = new[]
        {
            (manager, "ZONE_LICENSE_EXPIRY"),
            (manager, "STAFF_ACCOUNT_LOCK_REMINDER"),
            (accountant, "WATER_MEASUREMENT_OVERDUE"),
            (accountant, "WATER_ALERT"),
            (trader, "WATER_MEASUREMENT_OVERDUE"),
        };
        foreach (var ((account, client), type) in attempts)
        {
            var response = await client.SavePushAsync((type, true));
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{account.Username} {type}: {(int)response.StatusCode}");
            var problem = await response.ProblemAsync();
            Assert.Equal(("MSG-GEN-01", "Notification type"), (problem.GetProperty("code").GetString(), problem.GetProperty("field").GetString()));
        }

        foreach (var account in new[] { manager.Account, accountant.Account, trader.Account })
        {
            Assert.Empty(await StoredPreferencesAsync(account.Id));
        }
    }

    // ACCOUNT-07 step 3 saves the preferences as one choice: a refused item leaves the others unsaved.
    [Fact]
    public async Task One_refused_item_saves_none_of_the_request()
    {
        var (owner, client) = await AccountAsync(SubjectType.CooperativeOwner);

        var refused = await client.SavePushAsync(("WATER_MEASUREMENT_OVERDUE", false), ("WATER_ALERT", false));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal((false, true), (await client.OfferedTypesAsync())["WATER_MEASUREMENT_OVERDUE"]);
        Assert.Empty(await StoredPreferencesAsync(owner.Id));
    }

    [Fact]
    public async Task A_malformed_item_answers_400_and_an_empty_request_saves_nothing()
    {
        var (owner, client) = await AccountAsync(SubjectType.CooperativeOwner);

        foreach (var body in new[] { """{"items":[null]}""", """{"items":[{"push":true}]}""", """{"items":[{"type":"","push":false}]}""" })
        {
            var response = await client.PutAsync(NotificationsV2QaKit.PreferencesPath, new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{body}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        var empty = await client.PutAsync(NotificationsV2QaKit.PreferencesPath, new StringContent("""{"items":null}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal("MSG-GEN-05", (await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messageCode").GetString());
        Assert.Empty(await StoredPreferencesAsync(owner.Id));
    }

    // Mandatory types cannot be edited: push on is accepted but not stored, a stale stored "off" is shown as pushed, and
    // MSG-ACCOUNT-21 is only for a type the user turned on (ACCOUNT-07.1), never for a mandatory one.
    [Fact]
    public async Task A_mandatory_type_is_shown_pushed_whatever_is_stored_and_turning_it_on_saves_nothing()
    {
        var (owner, client) = await AccountAsync(SubjectType.CooperativeOwner);
        await factory.UpdateAsync<Account>(owner.Id, a => a.NotificationPreferences.Add(new NotificationPreference { Type = "ZONE_LICENSE_EXPIRY", Push = false }));

        var saved = await client.SavePushAsync(("WATER_ALERT", true));
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        var types = await client.OfferedTypesAsync();

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("noticeCode").ValueKind);
        Assert.False(body.GetProperty("pushAllowedOnDevice").GetBoolean());
        Assert.Equal((true, true), types["ZONE_LICENSE_EXPIRY"]);
        Assert.Equal((true, true), types["WATER_ALERT"]);
        Assert.DoesNotContain(await StoredPreferencesAsync(owner.Id), p => p.Type == "WATER_ALERT");
    }

    // The types follow the Effective pairs (GFR-02): once the Zone Manager pair is revoked, the water types are no longer
    // offered, and the stored choice is kept for a later grant.
    [Fact]
    public async Task Revoking_the_zone_manager_pair_takes_its_types_away()
    {
        var (_, ownerClient) = await AccountAsync(SubjectType.CooperativeOwner);
        var zone = await factory.AddZoneAsync(NewCode());
        var staff = await factory.AddAccountAsync($"u{NewCode()}", TestPassword, SubjectType.Staff);
        var pair = await factory.GrantAsync(staff, StaffRole.ZoneManager, zone);
        await factory.GrantAsync(staff, StaffRole.Accountant, null);
        var client = await factory.LoginAsync(staff.Username, TestPassword);
        Assert.Equal(HttpStatusCode.OK, (await client.SavePushAsync(("WATER_MEASUREMENT_OVERDUE", false))).StatusCode);
        Assert.Equal(["WATER_ALERT", "WATER_ALERT_WITHDRAWN", "WATER_MEASUREMENT_OVERDUE"], (await client.OfferedTypesAsync()).Keys);

        var revoked = await ownerClient.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants/{pair.Id}/revoke", new { reason = "Moved to the office" });

        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Empty(await client.OfferedTypesAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SavePushAsync(("WATER_MEASUREMENT_OVERDUE", true))).StatusCode);
        Assert.Contains(await StoredPreferencesAsync(staff.Id), p => p is { Type: "WATER_MEASUREMENT_OVERDUE", Push: false });
    }

    private async Task<(Account Account, HttpClient Client)> AccountAsync(SubjectType subjectType)
    {
        var account = await factory.AddAccountAsync($"u{NewCode()}", TestPassword, subjectType);
        return (account, await factory.LoginAsync(account.Username, TestPassword));
    }

    private async Task<(Account Account, HttpClient Client)> StaffAsync(StaffRole role, FarmingZone? zone)
    {
        var account = await factory.AddAccountAsync($"u{NewCode()}", TestPassword, SubjectType.Staff);
        await factory.GrantAsync(account, role, zone);
        return (account, await factory.LoginAsync(account.Username, TestPassword));
    }

    private Task<List<NotificationPreference>> StoredPreferencesAsync(Guid accountId) =>
        factory.QueryAsync(async db => (await db.Accounts.FindAsync(accountId))!.NotificationPreferences.ToList());
}
