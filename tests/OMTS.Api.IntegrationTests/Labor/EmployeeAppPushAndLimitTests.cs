using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.EmployeeApp;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Employees.EmployeesV2QaKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.EmployeeAppKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>BR-LABOR-04 and BR-LABOR-02: the push to the employee app (its token, the notifier, the development inbox) and the per-IP limits of the two endpoints that issue or check a code.</summary>
public class EmployeeAppPushAndLimitTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly UserMessage Assigned = new("MSG-LABOR-05", "You are assigned to raft B1-01 as a harvester today.");

    private static async Task NotifyAsync(AuthApiFactory api, Guid employeeId)
    {
        using var scope = api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IEmployeeNotifier>().NotifyAsync(
            employeeId, new EmployeeNotification("WORK_ASSIGNMENT", "Work assigned", Assigned, "Raft", Guid.NewGuid(), "B1-01"), CancellationToken.None);
        // The push is sent after a save commits, as every push is; any save does it.
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        (await db.EmployeeDevices.FirstAsync(d => d.EmployeeId == employeeId)).LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private static IReadOnlyList<FakeMessage> PushesTo(AuthApiFactory api, Guid employeeId) =>
        api.Services.GetRequiredService<FakePushSender>().Inbox.List(employeeId.ToString());

    [Fact]
    public async Task The_push_token_is_set_cleared_and_limited_on_the_callers_own_device()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(factory, world, worker);
        var phone = factory.AsEmployee(linked);

        Assert.Equal(HttpStatusCode.NoContent, (await phone.PutAsJsonAsync(PushToken, new { pushToken = " ExponentPushToken[abc] ", pushPermissionGranted = true })).StatusCode);
        var set = await factory.DeviceAsync(linked);
        Assert.Equal(("ExponentPushToken[abc]", true), (set.PushToken, set.PushPermissionGranted));

        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PutAsJsonAsync(PushToken, new { pushToken = new string('x', 501), pushPermissionGranted = true })).StatusCode);
        Assert.Equal("ExponentPushToken[abc]", (await factory.DeviceAsync(linked)).PushToken);

        Assert.Equal(HttpStatusCode.NoContent, (await phone.PutAsJsonAsync(PushToken, new { pushToken = "", pushPermissionGranted = true })).StatusCode);
        var cleared = await factory.DeviceAsync(linked);
        Assert.Equal((null, false), (cleared.PushToken, cleared.PushPermissionGranted));
    }

    [Fact]
    public async Task A_push_goes_to_the_linked_device_with_an_open_session_and_nothing_is_kept_in_a_list()
    {
        using var host = new AuthApiFactory();
        var world = await WorldAsync(host);
        var worker = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(host, world, worker);
        await host.AsEmployee(linked).PutAsJsonAsync(PushToken, new { pushToken = "phone-token", pushPermissionGranted = true });
        var rowsBefore = await host.QueryAsync(db => db.Notifications.CountAsync());

        await NotifyAsync(host, worker.Id);

        var push = Assert.Single(PushesTo(host, worker.Id));
        Assert.Equal(["phone-token"], push.Devices);
        Assert.Equal(("MSG-LABOR-05", "Work assigned"), (push.MessageCode, push.Subject));
        Assert.StartsWith("WORK_ASSIGNMENT: You are assigned", push.Text);
        Assert.Equal(rowsBefore, await host.QueryAsync(db => db.Notifications.CountAsync()));
        // The development inbox lists it by employee.
        var inbox = await host.CreateClient().GetFromJsonAsync<JsonElement>($"/api/dev/push?employeeId={worker.Id}");
        Assert.Equal(1, inbox.GetArrayLength());
        Assert.Equal("phone-token", inbox[0].GetProperty("devices")[0].GetString());
    }

    [Fact]
    public async Task A_device_without_permission_or_without_a_session_gets_no_token_but_the_inbox_keeps_the_push()
    {
        using var host = new AuthApiFactory();
        var world = await WorldAsync(host);
        var declined = await AddWorkerAsync(world);
        var loggedOut = await AddWorkerAsync(world);
        var withoutDevice = await AddWorkerAsync(world);
        var declinedLink = await LinkDeviceAsync(host, world, declined);
        await host.AsEmployee(declinedLink).PutAsJsonAsync(PushToken, new { pushToken = "declined-token", pushPermissionGranted = false });
        var outLink = await LinkDeviceAsync(host, world, loggedOut);
        await host.AsEmployee(outLink).PutAsJsonAsync(PushToken, new { pushToken = "logged-out-token", pushPermissionGranted = true });
        await host.AsEmployee(outLink).PostAsync(Logout, null);

        await NotifyAsync(host, declined.Id);
        await NotifyAsync(host, loggedOut.Id);
        using (var scope = host.Services.CreateScope())
        {
            // An employee who never linked a device: no device row to touch, so the push is queued and a save of anything sends it.
            await scope.ServiceProvider.GetRequiredService<IEmployeeNotifier>().NotifyAsync(withoutDevice.Id, new EmployeeNotification("WORK_ASSIGNMENT", "Work assigned", Assigned), CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
            (await db.EmployeeDevices.FirstAsync(d => d.EmployeeId == declined.Id)).LastSeenAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        foreach (var worker in new[] { declined, loggedOut, withoutDevice })
        {
            Assert.Empty(Assert.Single(PushesTo(host, worker.Id)).Devices!);
        }
    }

    // An unlinked device gets nothing: the token is gone with the link.
    [Fact]
    public async Task An_unlinked_device_loses_its_push_token()
    {
        using var host = new AuthApiFactory();
        var world = await WorldAsync(host);
        var worker = await AddWorkerAsync(world);
        var oldLink = await LinkDeviceAsync(host, world, worker);
        await host.AsEmployee(oldLink).PutAsJsonAsync(PushToken, new { pushToken = "old-phone", pushPermissionGranted = true });
        await LinkDeviceAsync(host, world, worker);

        Assert.Null((await host.DeviceAsync(oldLink)).PushToken);
        await NotifyAsync(host, worker.Id);
        // The new device has no token yet, so the push has no device at all.
        Assert.Empty(Assert.Single(PushesTo(host, worker.Id)).Devices!);
    }

    [Fact]
    public async Task Logins_over_the_per_minute_limit_get_429()
    {
        using var api = factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:LoginsPerMinute", "2"));
        var client = api.CreateClient();

        var answers = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.PostAsJsonAsync(Login, new { phoneNumber = NewPhone() })));

        Assert.Equal(2, answers.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
        var refused = Assert.Single(answers, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal("TOO_MANY_REQUESTS", await refused.ErrorCodeAsync());
        Assert.Equal("60", refused.Headers.RetryAfter?.ToString());
    }

    [Fact]
    public async Task Issuing_codes_over_the_per_minute_limit_gets_429_and_issues_no_second_code()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var password = NewPassword();
        var account = await factory.AddAccountAsync(NewCode(), password);
        using var api = factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:CodeRequestsPerMinute", "1"));
        var owner = WithBearer(api.CreateClient(), (await LoginTokensAsync(api.CreateClient(), account.Username, password)).GetProperty("accessToken").GetString()!);

        var answers = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => owner.PostAsync($"/api/employees/{worker.Id}/login-codes", null)));

        Assert.Single(answers, r => r.StatusCode == HttpStatusCode.Created);
        var refused = Assert.Single(answers, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal("TOO_MANY_REQUESTS", await refused.ErrorCodeAsync());
        Assert.Single(await factory.CodesAsync(worker.Id));
    }
}
