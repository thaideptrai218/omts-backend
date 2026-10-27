using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>
/// QA of the OMTS-214 push device binding (ACS-07): every way a session ends or continues, a device bound to a
/// session of another account, declined permission, mandatory push without a device and the SQL Server query.
/// </summary>
public class PushDeviceSessionQaTests
{
    // Generated per run, so no password-like literal is committed (secret scanners).
    private static readonly string ChangedPassword = AcsTestKit.NewPassword();

    /// <summary>A push was sent, to no device.</summary>
    private static readonly string[] NoDevices = [];

    // ACS-04 ends every other session but keeps this device logged in with fresh tokens (a new session): its
    // push must go on, as the user never logged out of it. The tablet's session ends, so its push stops.
    [Fact]
    public async Task Changing_the_password_on_the_phone_keeps_its_push_and_stops_the_other_devices()
    {
        await using var api = new AuthApiFactory();
        var account = await api.AddAccountAsync(NewCode(), Password);
        var phone = await LoginAndRegisterAsync(api.CreateClient(), account.Username, "phone-token");
        using var tablet = await LoginAndRegisterAsync(api.CreateClient(), account.Username, "tablet-token");

        var changed = await phone.PostAsJsonAsync("/api/me/password",
            new { currentPassword = Password, newPassword = ChangedPassword, passwordConfirmation = ChangedPassword, clientType = "Mobile" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        phone.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tokens").GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/me")).StatusCode);

        Assert.Equal(["phone-token"], await NotifyAsync(api, account.Id));
    }

    [Fact]
    public async Task A_refresh_keeps_the_device_and_a_replayed_refresh_token_stops_it()
    {
        await using var api = new AuthApiFactory();
        var account = await api.AddAccountAsync(NewCode(), Password);
        var client = api.CreateClient();
        var tokens = await LoginMobileAsync(client, account.Username);
        await RegisterAsync(client, tokens, "phone-token");
        var oldRefresh = tokens.GetProperty("refreshToken").GetString();

        var refreshed = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = oldRefresh });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var afterRefresh = await NotifyAsync(api, account.Id);

        // Reuse of the rotated token is treated as theft: the session is revoked (AuthService).
        var replayed = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = oldRefresh });
        var afterReplay = await NotifyAsync(api, account.Id);

        Assert.Equal(["phone-token"], afterRefresh);
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
        Assert.Equal(NoDevices, afterReplay);
    }

    // A locked account is not notified at all (NotificationRepository); once unlocked it is pushed again, but
    // its device stays silent until a new login registers it, because the lock ended its session.
    [Fact]
    public async Task Locking_a_staff_account_stops_its_device_and_unlocking_needs_a_new_login()
    {
        await using var api = new AuthApiFactory();
        var owner = await api.LoginAsync((await api.AddAccountAsync(NewCode(), Password)).Username);
        var staff = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var role = await api.AddRoleAsync($"Water {NewCode()}", (PermissionFunctions.WaterMeasurements, true, true, false));
        await api.GrantAsync(staff, role, await api.AddZoneAsync(NewCode()));
        using var staffPhone = await LoginAndRegisterAsync(api.CreateClient(), staff.Username, "staff-phone");
        var beforeLock = await NotifyAsync(api, staff.Id);

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/lock", new { reason = "QA" })).StatusCode);
        var locked = await NotifyAsync(api, staff.Id);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/unlock", new { reason = "QA" })).StatusCode);
        var unlocked = await NotifyAsync(api, staff.Id);

        Assert.Equal(["staff-phone"], beforeLock);
        Assert.Null(locked);
        Assert.Equal(NoDevices, unlocked);
    }

    // Only a data fault can produce it (registration always binds the caller's own session), but the query
    // must never deliver one account's push to a device through another account's session.
    [Fact]
    public async Task A_device_bound_to_a_live_session_of_another_account_receives_nothing()
    {
        await using var api = new AuthApiFactory();
        var first = await api.AddAccountAsync(NewCode(), Password);
        var second = await api.AddAccountAsync(NewCode(), Password);
        using var firstPhone = await LoginAndRegisterAsync(api.CreateClient(), first.Username, "first-phone");
        using var secondWeb = await api.LoginAsync(second.Username);
        await api.UseDbAsync(db =>
        {
            var secondSession = db.LoginSessions.Single(s => s.AccountId == second.Id && s.RevokedAt == null);
            db.PushDevices.Single(d => d.Token == "first-phone").LoginSessionId = secondSession.Id;
        });

        Assert.Equal(NoDevices, await NotifyAsync(api, first.Id));
        Assert.Equal(NoDevices, await NotifyAsync(api, second.Id));
    }

    [Fact]
    public async Task A_logged_in_device_that_declined_notifications_receives_nothing_and_push_is_not_eligible()
    {
        await using var api = new AuthApiFactory();
        var account = await api.AddAccountAsync(NewCode(), Password);
        var client = api.CreateClient();
        var tokens = await LoginMobileAsync(client, account.Username);
        await RegisterAsync(client, tokens, "declined-token", permissionGranted: false);

        Assert.Equal(NoDevices, await NotifyAsync(api, account.Id));
        Assert.Equal("NO_PUSH_DEVICE", (await PushReasonAsync(client)).GetString());
    }

    // Mandatory categories are always pushed; without a logged-in device the dev inbox still keeps the push,
    // with an empty "devices" list (README "External services").
    [Fact]
    public async Task Mandatory_push_after_logout_is_kept_in_the_dev_inbox_with_no_devices()
    {
        await using var api = new AuthApiFactory();
        var account = await api.AddAccountAsync(NewCode(), Password);
        var phone = await LoginAndRegisterAsync(api.CreateClient(), account.Username, "phone-token");
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsync("/api/auth/logout", null)).StatusCode);

        var tokens = await NotifyAsync(api, account.Id, NotificationTypes.WaterAlert);
        var inbox = await api.CreateClient().GetFromJsonAsync<JsonElement>($"/api/dev/push?accountId={account.Id}");

        Assert.Equal(NoDevices, tokens);
        Assert.Equal(JsonValueKind.Array, inbox[0].GetProperty("devices").ValueKind);
        Assert.Equal(0, inbox[0].GetProperty("devices").GetArrayLength());
        Assert.True(await api.QueryAsync(db => db.Notifications.AnyAsync(n => n.RecipientAccountId == account.Id && n.IsMandatory)));
    }

    // SQL Server translation of the settings eligibility (a one-element collection) and of one dispatch to
    // several recipients, plus the foreign key the migration created.
    [SqlServerFact]
    public async Task On_SQL_Server_settings_eligibility_and_a_multi_recipient_push_follow_the_sessions()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateAsync();
        await using var api = new SqlServerApiFactory(database);
        var first = await api.AddAccountAsync(NewCode(), Password);
        var second = await api.AddAccountAsync(NewCode(), Password);
        var phone = await LoginAndRegisterAsync(api.CreateClient(), first.Username, "first-phone");
        var tablet = await LoginAndRegisterAsync(api.CreateClient(), first.Username, "first-tablet");
        using var secondPhone = await LoginAndRegisterAsync(api.CreateClient(), second.Username, "second-phone");
        var web = api.CreateClient();
        web.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await LoginTokensAsync(web, first.Username)).GetProperty("accessToken").GetString());

        var eligible = await PushReasonAsync(web);
        await phone.PostAsync("/api/auth/logout", null);
        var pushed = await NotifyManyAsync(api, [first.Id, second.Id]);
        await tablet.PostAsync("/api/auth/logout", null);
        var ineligible = await PushReasonAsync(web);
        var deleteAction = await database.ReadAsync(db => db.Database
            .SqlQueryRaw<string>("SELECT delete_referential_action_desc AS Value FROM sys.foreign_keys WHERE name = 'FK_PushDevices_LoginSessions_LoginSessionId'")
            .SingleAsync());

        Assert.Equal(JsonValueKind.Null, eligible.ValueKind);
        Assert.Equal(["first-tablet"], pushed[first.Id]);
        Assert.Equal(["second-phone"], pushed[second.Id]);
        Assert.Equal("NO_PUSH_DEVICE", ineligible.GetString());
        Assert.Equal("NO_ACTION", deleteAction);
    }

    private static async Task<JsonElement> LoginMobileAsync(HttpClient client, string username)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { identifier = username, password = Password, clientType = "Mobile" });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task RegisterAsync(HttpClient client, JsonElement tokens, string token, bool permissionGranted = true)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        var response = await client.PostAsJsonAsync("/api/me/devices", new { token, platform = "android", permissionGranted });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<HttpClient> LoginAndRegisterAsync(HttpClient client, string username, string token)
    {
        await RegisterAsync(client, await LoginMobileAsync(client, username), token);
        return client;
    }

    /// <summary>
    /// Sends one notification to the account and returns the device tokens of its push, or null when no push
    /// was sent at all (e.g. a locked account is not notified).
    /// </summary>
    private static async Task<IReadOnlyList<string>?> NotifyAsync(
        WebApplicationFactory<Program> api, Guid accountId, string type = NotificationTypes.WorkAssignment) =>
        (await NotifyManyAsync(api, [accountId], type))[accountId];

    private static async Task<Dictionary<Guid, IReadOnlyList<string>?>> NotifyManyAsync(
        WebApplicationFactory<Program> api, Guid[] accountIds, string type = NotificationTypes.WorkAssignment)
    {
        var inbox = api.Services.GetRequiredService<FakePushSender>().Inbox;
        var before = accountIds.ToDictionary(id => id, id => inbox.List(id.ToString()).FirstOrDefault()?.Id);
        using (var scope = api.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<INotificationDispatcher>().NotifyAsync(
                accountIds, type, "QA", null, null, null, isMandatory: false, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        return accountIds.ToDictionary(id => id, id => inbox.List(id.ToString()).FirstOrDefault() is { } latest && latest.Id != before[id]
            ? (IReadOnlyList<string>?)(latest.Devices ?? []).Order().ToList()
            : null);
    }

    private static async Task<JsonElement> PushReasonAsync(HttpClient client)
    {
        var categories = await client.GetFromJsonAsync<JsonElement>("/api/me/notification-preferences");
        var push = categories[0].GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("channel").GetString() == nameof(NotificationChannel.Push));
        return push.GetProperty("ineligibleReason");
    }
}
