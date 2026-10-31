using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>ACCOUNT-05 personal profile and ACCOUNT-07 notification preferences (push per type).</summary>
public class ProfileAndPreferenceTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Display_name_is_saved_with_the_read_row_version_and_username_is_read_only()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(owner.Username);
        var profile = await client.GetFromJsonAsync<JsonElement>("/api/me/profile");
        var rowVersion = profile.GetProperty("rowVersion").GetString();

        var username = await client.PutAsJsonAsync("/api/me/profile", new { displayName = "New name", rowVersion, username = "hacker" });
        var empty = await client.PutAsJsonAsync("/api/me/profile", new { displayName = " ", rowVersion });
        var stale = await client.PutAsJsonAsync("/api/me/profile", new { displayName = "New name", rowVersion = "AAAAAAAAB9E=" });
        var saved = await client.PutAsJsonAsync("/api/me/profile", new { displayName = "Chủ nhiệm Nguyễn", rowVersion });

        Assert.Equal("MSG-GEN-01", await username.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-01", await empty.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
        Assert.Equal("Personal profile saved.", (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        Assert.Equal("Chủ nhiệm Nguyễn", (await client.GetFromJsonAsync<JsonElement>("/api/me/profile")).GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task New_email_is_used_only_after_its_code_is_verified()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(owner.Username);
        var email = $"new-{NewCode()}@mail.com";

        var started = await (await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Email", target = email, currentPassword = Password })).Content.ReadFromJsonAsync<JsonElement>();
        var message = started.GetProperty("message").GetString();
        var before = await client.GetFromJsonAsync<JsonElement>("/api/me/profile");
        var token = await VerifyAsync(client, started.GetProperty("verification").GetProperty("verificationId").GetGuid(), factory.LastEmailCode(email));
        var confirmed = await client.PostAsJsonAsync("/api/me/contact-changes/confirm", new { verificationToken = token });
        var after = await client.GetFromJsonAsync<JsonElement>("/api/me/profile");

        Assert.StartsWith("A verification code has been sent to n***@mail.com", message);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("email").ValueKind);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(email, after.GetProperty("email").GetString());
        Assert.True(after.GetProperty("emailVerified").GetBoolean());
    }

    [Fact]
    public async Task Contact_of_another_account_or_the_same_verified_value_is_refused_without_a_code()
    {
        var other = await factory.AddAccountAsync(NewCode(), Password);
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(owner.Username);

        var taken = await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Sms", target = other.PhoneNumber });
        var same = await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Sms", target = owner.PhoneNumber });

        Assert.Equal("MSG-GEN-01", await taken.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-01", await same.ErrorCodeAsync());
    }

    [Fact]
    public async Task Trader_phone_change_merges_the_unique_offline_profile()
    {
        var cooperative = await factory.EnsureCooperativeAsync();
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var own = new Customer { CooperativeId = cooperative.Id, AccountId = trader.Id, Name = "Own", PhoneNumber = trader.PhoneNumber };
        var phone = NewPhone();
        var offline = new Customer { CooperativeId = cooperative.Id, Name = "Offline", PhoneNumber = phone };
        await factory.UseDbAsync(db => db.Customers.AddRange(own, offline));
        var client = await factory.LoginAsync(trader.Username);

        var started = await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Sms", target = phone, currentPassword = Password });
        var token = await VerifyAsync(client, await VerificationIdAsync(started), factory.LastSmsCode(phone));
        await client.PostAsJsonAsync("/api/me/contact-changes/confirm", new { verificationToken = token });

        var stored = await factory.QueryAsync(db => db.Customers.AsNoTracking().Where(c => c.Id == offline.Id || c.Id == own.Id).ToListAsync());
        Assert.Equal(own.Id, stored.Single(c => c.Id == offline.Id).MergedIntoCustomerId);
        Assert.Equal(phone, stored.Single(c => c.Id == own.Id).PhoneNumber);
    }

    // Generated per run, so no password-like literal is committed (secret scanners).
    private static readonly string PreferencePassword = NewPassword();

    // ACCOUNT-07 step 1: the types of the roles the account holds; mandatory ones are always pushed; never-push types
    // (the lock reminder) are not offered.
    [Fact]
    public async Task Preferences_offer_the_types_of_the_roles_held_with_mandatory_ones_always_pushed()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var owner = await factory.AddAccountAsync(NewCode(), PreferencePassword);
        var manager = await factory.AddAccountAsync(NewCode(), PreferencePassword, SubjectType.Staff);
        var accountant = await factory.AddAccountAsync(NewCode(), PreferencePassword, SubjectType.Staff);
        var trader = await factory.AddAccountAsync(NewCode(), PreferencePassword, SubjectType.Trader);
        await factory.GrantAsync(manager, StaffRole.ZoneManager, zone);
        await factory.GrantAsync(accountant, StaffRole.Accountant, null);

        var ownerTypes = await TypesAsync(await factory.LoginAsync(owner.Username, PreferencePassword));
        var managerTypes = await TypesAsync(await factory.LoginAsync(manager.Username, PreferencePassword));
        var accountantTypes = await TypesAsync(await factory.LoginAsync(accountant.Username, PreferencePassword));
        var traderTypes = await TypesAsync(await factory.LoginAsync(trader.Username, PreferencePassword));

        Assert.Equal(["WATER_ALERT", "WATER_ALERT_WITHDRAWN", "WATER_MEASUREMENT_OVERDUE", "ZONE_LICENSE_EXPIRY"], ownerTypes.Keys);
        Assert.Equal(["WATER_ALERT", "WATER_ALERT_WITHDRAWN", "WATER_MEASUREMENT_OVERDUE"], managerTypes.Keys);
        Assert.Empty(accountantTypes);
        Assert.Empty(traderTypes);
        Assert.Equal((true, true), ownerTypes["WATER_ALERT"]);
        Assert.Equal((true, true), ownerTypes["ZONE_LICENSE_EXPIRY"]);
        Assert.Equal((false, true), ownerTypes["WATER_MEASUREMENT_OVERDUE"]);
    }

    [Fact]
    public async Task Push_of_a_normal_type_is_turned_off_and_on_again_and_mandatory_or_other_types_are_refused()
    {
        var owner = await factory.AddAccountAsync(NewCode(), PreferencePassword);
        var client = await factory.LoginAsync(owner.Username, PreferencePassword);

        var off = await (await SaveAsync(client, ("WATER_MEASUREMENT_OVERDUE", false))).Content.ReadFromJsonAsync<JsonElement>();
        var afterOff = await TypesAsync(client);
        var mandatoryOff = await SaveAsync(client, ("WATER_ALERT", false));
        var neverPush = await SaveAsync(client, ("STAFF_ACCOUNT_LOCK_REMINDER", true));
        var unknown = await SaveAsync(client, ("WORK", true));
        await SaveAsync(client, ("WATER_ALERT", true), ("WATER_MEASUREMENT_OVERDUE", true));
        var afterOn = await TypesAsync(client);

        Assert.Equal(("Notification settings saved.", "MSG-GEN-05"), (off.GetProperty("message").GetString(), off.GetProperty("messageCode").GetString()));
        Assert.Equal((false, false), afterOff["WATER_MEASUREMENT_OVERDUE"]);
        foreach (var refused in new[] { mandatoryOff, neverPush, unknown })
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("MSG-GEN-01", await refused.ErrorCodeAsync());
            Assert.Equal("Notification type", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        }

        Assert.Equal((true, true), afterOn["WATER_ALERT"]);
        Assert.Equal((false, true), afterOn["WATER_MEASUREMENT_OVERDUE"]);
    }

    // ACCOUNT-07.1: the choice is saved and MSG-ACCOUNT-21 replaces MSG-GEN-05 in the notice.
    [Fact]
    public async Task Push_turned_on_without_an_allowed_device_is_saved_with_msg_account_21()
    {
        var owner = await factory.AddAccountAsync(NewCode(), PreferencePassword);
        var client = await factory.LoginAsync(owner.Username, PreferencePassword);
        await client.PostAsJsonAsync("/api/me/devices", new { token = $"ExponentPushToken[{NewCode()}]", platform = "android", permissionGranted = false });
        await SaveAsync(client, ("WATER_MEASUREMENT_OVERDUE", false));

        var saved = await SaveAsync(client, ("WATER_MEASUREMENT_OVERDUE", true));
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.StartsWith("Your preferences are saved. This device does not allow notifications from OMTS.", body.GetProperty("notice").GetString());
        Assert.Equal("MSG-ACCOUNT-21", body.GetProperty("noticeCode").GetString());
        Assert.False(body.GetProperty("pushAllowedOnDevice").GetBoolean());
        Assert.Equal((false, true), (await TypesAsync(client))["WATER_MEASUREMENT_OVERDUE"]);

        // Once a device allows notifications, no notice; turning push off never gives one.
        var token = $"ExponentPushToken[{NewCode()}]";
        await client.PostAsJsonAsync("/api/me/devices", new { token, platform = "ios", permissionGranted = true });
        var allowed = await (await SaveAsync(client, ("WATER_MEASUREMENT_OVERDUE", true))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, allowed.GetProperty("notice").ValueKind);
        Assert.True(allowed.GetProperty("pushAllowedOnDevice").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/me/devices/{Uri.EscapeDataString(token)}")).StatusCode);
        var turnedOff = await (await SaveAsync(client, ("WATER_MEASUREMENT_OVERDUE", false))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, turnedOff.GetProperty("noticeCode").ValueKind);
    }

    /// <summary>The offered types in order, each with (mandatory, push).</summary>
    private static async Task<Dictionary<string, (bool Mandatory, bool Push)>> TypesAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/me/notification-preferences")).GetProperty("types").EnumerateArray()
            .ToDictionary(t => t.GetProperty("type").GetString()!, t => (t.GetProperty("mandatory").GetBoolean(), t.GetProperty("push").GetBoolean()));

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, params (string Type, bool Push)[] items) =>
        client.PutAsJsonAsync("/api/me/notification-preferences", new { items = items.Select(i => new { type = i.Type, push = i.Push }) });
}