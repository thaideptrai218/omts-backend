using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cooperatives;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Employees.EmployeesV2QaKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.EmployeeAppKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-06: refresh and logout of the employee app. The rotation, the grace period and the replay rule are those of a staff session; the clock is moved by hand, the sessions are real.</summary>
public class EmployeeAppSessionTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private static async Task<(MovableClockApiFactory Api, World World, Worker Worker, JsonElement Linked)> StartAsync()
    {
        var api = new MovableClockApiFactory();
        var world = await WorldAsync(api);
        var worker = await AddWorkerAsync(world);
        return (api, world, worker, await LinkDeviceAsync(api, world, worker));
    }

    private static async Task<JsonElement> RefreshedAsync(HttpClient client, string refreshToken) =>
        await (await RefreshAsync(client, refreshToken)).JsonAsync(HttpStatusCode.OK);

    [Fact]
    public async Task A_refresh_rotates_the_token_and_the_new_access_token_works()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(factory, world, worker);

        var refreshed = await RefreshedAsync(factory.CreateClient(), linked.GetProperty("refreshToken").GetString()!);

        Assert.True(refreshed.GetProperty("refreshTokenRotated").GetBoolean());
        Assert.NotEqual(linked.GetProperty("refreshToken").GetString(), refreshed.GetProperty("refreshToken").GetString());
        Assert.Equal(linked.GetProperty("deviceId").GetGuid(), refreshed.GetProperty("deviceId").GetGuid());
        Assert.Equal(JsonValueKind.Null, refreshed.GetProperty("deviceSecret").ValueKind);
        Assert.Equal(worker.Id, refreshed.GetProperty("employee").GetProperty("id").GetGuid());
        var push = await factory.AsEmployee(refreshed).PutAsJsonAsync(PushToken, new { pushToken = (string?)null, pushPermissionGranted = false });
        Assert.Equal(HttpStatusCode.NoContent, push.StatusCode);
    }

    [Fact]
    public async Task The_replaced_token_works_once_more_inside_the_grace_period_and_does_not_rotate_again()
    {
        var (api, _, _, linked) = await StartAsync();
        await using var owned = api;
        var client = api.CreateClient();
        var first = linked.GetProperty("refreshToken").GetString()!;
        var start = DateTimeOffset.UtcNow;
        api.Clock.Set(start);
        var rotated = await RefreshedAsync(client, first);

        api.Clock.Set(start + Grace - TimeSpan.FromSeconds(1));
        var grace = await RefreshedAsync(client, first);

        Assert.False(grace.GetProperty("refreshTokenRotated").GetBoolean());
        Assert.Equal(first, grace.GetProperty("refreshToken").GetString());
        Assert.Equal(rotated.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset(), grace.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset());
        // The token the winner got is still the current one.
        Assert.True((await RefreshedAsync(client, rotated.GetProperty("refreshToken").GetString()!)).GetProperty("refreshTokenRotated").GetBoolean());
    }

    [Fact]
    public async Task The_replaced_token_after_the_grace_period_is_replay_and_ends_the_session()
    {
        var (api, _, worker, linked) = await StartAsync();
        await using var owned = api;
        var client = api.CreateClient();
        var start = DateTimeOffset.UtcNow;
        api.Clock.Set(start);
        var rotated = await RefreshedAsync(client, linked.GetProperty("refreshToken").GetString()!);

        api.Clock.Set(start + Grace + TimeSpan.FromSeconds(1));
        var late = await RefreshAsync(client, linked.GetProperty("refreshToken").GetString()!);
        var successor = await RefreshAsync(client, rotated.GetProperty("refreshToken").GetString()!);

        Assert.Equal(HttpStatusCode.Unauthorized, late.StatusCode);
        Assert.Equal("MSG19", await late.ErrorCodeAsync());
        Assert.Equal("reuse", await ReasonAsync(late));
        Assert.Equal(HttpStatusCode.Unauthorized, successor.StatusCode);
        Assert.Equal("reuse", await ReasonAsync(successor));
        var session = Assert.Single(await api.SessionsAsync(worker.Id));
        Assert.Equal("Refresh token reuse detected", session.RevokeReason);
        Assert.Equal(OMTS.Domain.Enums.SessionRevokeCause.TokenReuse, session.RevokeCause);
        // The device stays linked: a replay ends the session, not the link.
        Assert.True((await api.DeviceAsync(linked)).IsLinked);
    }

    // Only the token the last rotation replaced is forgiven, however recently the older ones were replaced.
    [Fact]
    public async Task A_token_older_than_the_previous_one_ends_the_session_at_once()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(factory, world, worker);
        var client = factory.CreateClient();
        var second = await RefreshedAsync(client, linked.GetProperty("refreshToken").GetString()!);
        var third = await RefreshedAsync(client, second.GetProperty("refreshToken").GetString()!);

        var replay = await RefreshAsync(client, linked.GetProperty("refreshToken").GetString()!);
        var current = await RefreshAsync(client, third.GetProperty("refreshToken").GetString()!);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("reuse", await ReasonAsync(replay));
        Assert.Equal(HttpStatusCode.Unauthorized, current.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.AsEmployee(third).PostAsync(Logout, null)).StatusCode);
    }

    [Fact]
    public async Task A_made_up_token_is_expired_and_a_staff_refresh_token_is_nothing_to_the_employee_app()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(factory, world, worker);
        var password = NewPassword();
        var account = await factory.AddAccountAsync(NewCode(), password);
        var staff = await LoginTokensAsync(factory.CreateClient(), account.Username, password);
        var client = factory.CreateClient();

        var junk = await RefreshAsync(client, "not.a.token");
        var staffToken = await RefreshAsync(client, staff.GetProperty("refreshToken").GetString()!);
        var employeeToken = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = linked.GetProperty("refreshToken").GetString() });

        foreach (var refused in new[] { junk, staffToken })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("expired", await ReasonAsync(refused));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, employeeToken.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Refresh, new { refreshToken = "" })).StatusCode);
        // Neither refusal touched a session: both tokens are still good where they belong.
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, linked.GetProperty("refreshToken").GetString()!)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = staff.GetProperty("refreshToken").GetString() })).StatusCode);
    }

    // LABOR-06.1: logging out ends the session and keeps the device, so the next login on it needs no code.
    [Fact]
    public async Task Logout_ends_the_session_and_keeps_the_device_linked()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(factory, world, worker);
        var phone = factory.AsEmployee(linked);

        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsync(Logout, null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.PostAsync(Logout, null)).StatusCode);
        var refresh = await RefreshAsync(factory.CreateClient(), linked.GetProperty("refreshToken").GetString()!);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal("revoked", await ReasonAsync(refresh));
        Assert.True((await factory.DeviceAsync(linked)).IsLinked);
        var again = await LoginOnDeviceAsync(factory.CreateClient(), worker.Phone, linked);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await factory.AsEmployee(await again.Content.ReadFromJsonAsync<JsonElement>()).PostAsync(Logout, null)).StatusCode);
        Assert.Equal(2, (await factory.SessionsAsync(worker.Id)).Count(s => s.RevokedAt is not null));
    }

    // M3: one device has one session: a code-less login ends the earlier one, so a logout of the old tokens is refused and the new session is untouched.
    [Fact]
    public async Task A_second_login_on_the_device_replaces_its_session_and_a_logout_ends_only_the_open_one()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(factory, world, worker);
        var other = await (await LoginOnDeviceAsync(factory.CreateClient(), worker.Phone, linked)).JsonAsync(HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.AsEmployee(linked).PostAsync(Logout, null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await factory.AsEmployee(other).PostAsync(Logout, null)).StatusCode);
        Assert.Equal(2, (await factory.SessionsAsync(worker.Id)).Count(s => s.RevokedAt is not null));
    }

    // A token whose session does not exist (a signed token for a session id nobody opened) is refused like a revoked one.
    [Fact]
    public async Task An_access_token_expires_with_its_session_even_when_the_signature_is_good()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(factory, world, worker);
        var session = Assert.Single(await factory.SessionsAsync(worker.Id));
        await factory.UpdateAsync<OMTS.Domain.Entities.Labor.EmployeeSession>(session.Id, s => s.AbsoluteExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1));

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.AsEmployee(linked).PostAsync(Logout, null)).StatusCode);
        var refresh = await RefreshAsync(factory.CreateClient(), linked.GetProperty("refreshToken").GetString()!);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal("expired", await ReasonAsync(refresh));
    }
}
