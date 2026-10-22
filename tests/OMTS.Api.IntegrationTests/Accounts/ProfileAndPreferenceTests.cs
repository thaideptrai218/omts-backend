using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>ACS-05 personal profile and ACS-07 notification preferences.</summary>
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

        Assert.Equal("MSG20", await username.ErrorCodeAsync());
        Assert.Equal("MSG01", await empty.ErrorCodeAsync());
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

        var started = await (await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Email", target = email })).Content.ReadFromJsonAsync<JsonElement>();
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

        Assert.Equal("MSG01", await taken.ErrorCodeAsync());
        Assert.Equal("MSG01", await same.ErrorCodeAsync());
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

        var started = await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Sms", target = phone });
        var token = await VerifyAsync(client, await VerificationIdAsync(started), factory.LastSmsCode(phone));
        await client.PostAsJsonAsync("/api/me/contact-changes/confirm", new { verificationToken = token });

        var stored = await factory.QueryAsync(db => db.Customers.AsNoTracking().Where(c => c.Id == offline.Id || c.Id == own.Id).ToListAsync());
        Assert.Equal(own.Id, stored.Single(c => c.Id == offline.Id).MergedIntoCustomerId);
        Assert.Equal(phone, stored.Single(c => c.Id == own.Id).PhoneNumber);
    }

    [Fact]
    public async Task Mandatory_items_cannot_be_turned_off_and_ineligible_channels_cannot_be_turned_on()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(owner.Username);

        var mandatory = await SaveAsync(client, ("WATER_ALERTS", "InApp", false));
        var email = await SaveAsync(client, ("WORK", "Email", true));
        var zalo = await SaveAsync(client, ("WORK", "Zalo", true));

        Assert.Equal("MSG23", await mandatory.ErrorCodeAsync());
        Assert.Equal("MSG21", await email.ErrorCodeAsync());
        var zaloProblem = await zalo.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("The Zalo OA channel is not configured or not eligible, so it cannot be enabled yet.", zaloProblem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Push_without_an_allowed_device_is_saved_off_while_other_channels_are_saved()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(owner.Username);
        await client.PostAsJsonAsync("/api/me/devices", new { token = $"ExponentPushToken[{NewCode()}]", platform = "android", permissionGranted = false });

        var saved = await SaveAsync(client, ("WORK", "InApp", false), ("WORK", "Push", true));
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("The device has declined push notifications. Other valid channels are still saved.", body.GetProperty("notice").GetString());
        var work = body.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("category").GetString() == "WORK");
        var channels = work.GetProperty("channels").EnumerateArray().ToDictionary(c => c.GetProperty("channel").GetString()!, c => c.GetProperty("enabled").GetBoolean());
        Assert.False(channels["InApp"]);
        Assert.False(channels["Push"]);

        // Once the device allows notifications, push can be turned on.
        var token = $"ExponentPushToken[{NewCode()}]";
        await client.PostAsJsonAsync("/api/me/devices", new { token, platform = "ios", permissionGranted = true });
        Assert.Equal(JsonValueKind.Null, (await (await SaveAsync(client, ("WORK", "Push", true))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("notice").ValueKind);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/me/devices/{Uri.EscapeDataString(token)}")).StatusCode);
    }

    [Fact]
    public async Task Traders_see_only_their_categories()
    {
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var client = await factory.LoginAsync(trader.Username);

        var categories = (await client.GetFromJsonAsync<JsonElement>("/api/me/notification-preferences")).EnumerateArray().Select(c => c.GetProperty("category").GetString());

        Assert.Equal(["ORDERS", "TRACEABILITY_QR_SENT"], categories);
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, params (string Category, string Channel, bool Enabled)[] items) =>
        client.PutAsJsonAsync("/api/me/notification-preferences", new { items = items.Select(i => new { category = i.Category, channel = i.Channel, enabled = i.Enabled }) });
}
