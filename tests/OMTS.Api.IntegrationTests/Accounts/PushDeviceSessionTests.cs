using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>ACS-07 follow-up (OMTS-214): a push token belongs to the login session that registered it.</summary>
public class PushDeviceSessionTests
{
    [Fact]
    public async Task Logging_out_of_one_device_stops_its_push_and_keeps_the_other_device()
    {
        await using var api = new AuthApiFactory();
        var account = await api.AddAccountAsync("owner", AcsTestKit.Password);
        var phone = await LoginAndRegisterAsync(api, "owner", "phone-token");
        using var tablet = await LoginAndRegisterAsync(api, "owner", "tablet-token");

        var bothDevices = await NotifyAsync(api, account.Id);
        var logout = await phone.PostAsync("/api/auth/logout", null);
        var tabletOnly = await NotifyAsync(api, account.Id);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(["phone-token", "tablet-token"], bothDevices.Order());
        Assert.Equal(["tablet-token"], tabletOnly);
    }

    [Fact]
    public async Task Without_a_logged_in_device_push_is_not_eligible_in_the_settings()
    {
        await using var api = new AuthApiFactory();
        await api.AddAccountAsync("owner", AcsTestKit.Password);
        var phone = await LoginAndRegisterAsync(api, "owner", "phone-token");
        using var web = await api.LoginAsync("owner");

        var before = await PushAllowedAsync(web);
        await phone.PostAsync("/api/auth/logout", null);
        var after = await PushAllowedAsync(web);

        Assert.True(before);
        Assert.False(after);
    }

    [Fact]
    public async Task Token_moved_to_another_account_is_audited_and_bound_to_the_new_session()
    {
        await using var api = new AuthApiFactory();
        var first = await api.AddAccountAsync("first", AcsTestKit.Password);
        var second = await api.AddAccountAsync("second", AcsTestKit.Password);
        using var firstPhone = await LoginAndRegisterAsync(api, "first", "shared-phone");

        using var secondPhone = await LoginAndRegisterAsync(api, "second", "shared-phone");

        Assert.Empty(await NotifyAsync(api, first.Id));
        Assert.Equal(["shared-phone"], await NotifyAsync(api, second.Id));
        var moved = await api.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "PushDevice.Moved"));
        Assert.Contains(first.Id.ToString(), moved.Before);
        Assert.Contains(second.Id.ToString(), moved.After);
    }

    [Fact]
    public async Task Revoked_or_expired_session_and_a_device_from_before_the_binding_receive_nothing()
    {
        await using var api = new AuthApiFactory();
        var account = await api.AddAccountAsync("owner", AcsTestKit.Password);
        using var revoked = await LoginAndRegisterAsync(api, "owner", "revoked-token");
        using var expired = await LoginAndRegisterAsync(api, "owner", "expired-token");
        using var live = await LoginAndRegisterAsync(api, "owner", "live-token");
        var now = DateTimeOffset.UtcNow;
        await api.UseDbAsync(db =>
        {
            var sessions = db.PushDevices.Include(d => d.LoginSession).ToDictionary(d => d.Token, d => d.LoginSession!);
            sessions["revoked-token"].Revoke(now, "Password changed");
            sessions["expired-token"].RefreshTokenExpiresAt = now.AddMinutes(-1);
            db.PushDevices.Add(new PushDevice { AccountId = account.Id, Token = "legacy-token", Platform = "android", PermissionGranted = true });
        });

        Assert.Equal(["live-token"], await NotifyAsync(api, account.Id));
    }

    [Fact]
    public async Task Re_registering_after_a_new_login_moves_the_device_to_the_new_session()
    {
        await using var api = new AuthApiFactory();
        var account = await api.AddAccountAsync("owner", AcsTestKit.Password);
        var oldLogin = await LoginAndRegisterAsync(api, "owner", "phone-token");
        using var newLogin = await LoginAndRegisterAsync(api, "owner", "phone-token");

        await oldLogin.PostAsync("/api/auth/logout", null);

        Assert.Equal(["phone-token"], await NotifyAsync(api, account.Id));
        Assert.Equal(1, await api.QueryAsync(db => db.PushDevices.CountAsync()));
    }

    // The deliverable-device query (session join, revocation, refresh expiry) translated by SQL Server.
    [SqlServerFact]
    public async Task On_SQL_Server_logout_stops_the_push_of_that_device()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateAsync();
        await using var api = new SqlServerApiFactory(database);
        var account = await api.AddAccountAsync(AcsTestKit.NewCode(), AcsTestKit.Password);
        var phone = await LoginAndRegisterAsync(api.CreateClient(), account.Username, "phone-token");
        using var tablet = await LoginAndRegisterAsync(api.CreateClient(), account.Username, "tablet-token");

        await phone.PostAsync("/api/auth/logout", null);

        Assert.Equal(["tablet-token"], await NotifyAsync(api, account.Id));
    }

    private static Task<HttpClient> LoginAndRegisterAsync(AuthApiFactory api, string username, string token) =>
        LoginAndRegisterAsync(api.CreateClient(), username, token);

    private static async Task<HttpClient> LoginAndRegisterAsync(HttpClient client, string username, string token)
    {
        var tokens = await AcsTestKit.LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        var response = await client.PostAsJsonAsync("/api/me/devices", new { token, platform = "android", permissionGranted = true });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return client;
    }

    /// <summary>Sends one notification to the account and returns the device tokens of its push.</summary>
    private static async Task<IReadOnlyList<string>> NotifyAsync(WebApplicationFactory<Program> api, Guid accountId)
    {
        using (var scope = api.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<INotificationDispatcher>().NotifyAsync(
                [accountId],
                new NotificationContent(NotificationTypes.WaterMeasurementOverdue, "Reminder", UserMessages.WaterMeasurementOverdue("Zone", new DateOnly(2026, 10, 1))),
                CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        var latest = api.Services.GetRequiredService<FakePushSender>().Inbox.List(accountId.ToString())[0];
        return latest.Devices ?? [];
    }

    /// <summary>Whether the settings say that a device of the account allows notifications (ACCOUNT-07.1).</summary>
    private static async Task<bool> PushAllowedAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/me/notification-preferences")).GetProperty("pushAllowedOnDevice").GetBoolean();
}
