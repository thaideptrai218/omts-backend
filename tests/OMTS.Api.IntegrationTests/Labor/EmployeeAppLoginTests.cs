using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Employees.EmployeesV2QaKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.EmployeeAppKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-06 and BR-LABOR-02: the login with a phone number, the code, the linked device.</summary>
public class EmployeeAppLoginTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static async Task<string> CodeOfAsync(HttpResponseMessage response) => (await response.ErrorCodeAsync())!;

    [Fact]
    public async Task A_phone_number_no_employed_employee_has_is_refused_with_msg_labor_18()
    {
        var world = await WorldAsync(factory);
        var left = await AddWorkerAsync(world);
        await EndAsync(world.Owner, left.Id);
        var endedNotFlipped = await AddWorkerAsync(world);
        await factory.UpdateAsync<OMTS.Domain.Entities.Identity.Employee>(endedNotFlipped.Id, e => e.LastWorkingDay = Today().AddDays(-1));
        var phone = factory.CreateClient();

        foreach (var number in new[] { NewPhone(), left.Phone, endedNotFlipped.Phone })
        {
            var refused = await LoginWithCodeAsync(phone, number, "123456");

            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("MSG-LABOR-18", await CodeOfAsync(refused));
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await LoginWithCodeAsync(phone, "not a phone", "123456")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsJsonAsync(Login, new { })).StatusCode);
    }

    [Fact]
    public async Task A_device_that_is_not_linked_is_asked_for_a_code_with_msg_labor_17()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);

        var asked = await factory.CreateClient().PostAsJsonAsync(Login, new { phoneNumber = worker.Phone });

        var problem = await asked.JsonAsync(HttpStatusCode.Unauthorized);
        Assert.Equal("MSG-LABOR-17", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("loginCodeRequired").GetBoolean());
        // A device id with a secret that is nobody's gets the same answer.
        var guess = await factory.CreateClient().PostAsJsonAsync(Login, new { phoneNumber = worker.Phone, deviceId = Guid.NewGuid(), deviceSecret = "0".PadLeft(64, '0') });
        Assert.Equal("MSG-LABOR-17", await CodeOfAsync(guess));
    }

    [Fact]
    public async Task A_correct_code_links_the_device_and_logs_in_in_any_form_of_the_number()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var code = await IssueCodeAsync(world.Manager, worker.Id);

        var tokens = await (await LoginWithCodeAsync(factory.CreateClient(), $"+84 {worker.Phone[1..]}", code)).JsonAsync(HttpStatusCode.OK);

        Assert.False(string.IsNullOrEmpty(tokens.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(tokens.GetProperty("refreshToken").GetString()));
        Assert.True(tokens.GetProperty("refreshTokenRotated").GetBoolean());
        Assert.Equal((worker.Id, worker.Name, "SeasonalWorker"), (
            tokens.GetProperty("employee").GetProperty("id").GetGuid(),
            tokens.GetProperty("employee").GetProperty("fullName").GetString(),
            tokens.GetProperty("employee").GetProperty("type").GetString()));
        Assert.Equal(60, tokens.GetProperty("qrStepSeconds").GetInt32());
        Assert.Equal(43, tokens.GetProperty("qrKey").GetString()!.Length);
        Assert.InRange(tokens.GetProperty("serverTime").GetDateTimeOffset(), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        var device = await factory.DeviceAsync(tokens);
        Assert.Equal((worker.Id, "android", true), (device.EmployeeId, device.Platform, device.IsLinked));
        Assert.NotEqual(tokens.GetProperty("deviceSecret").GetString(), device.DeviceSecretHash);
        Assert.DoesNotContain(tokens.GetProperty("deviceSecret").GetString()!, device.DeviceSecretHash);
        var spent = Assert.Single(await factory.CodesAsync(worker.Id));
        Assert.NotNull(spent.ConsumedAt);
        Assert.Equal(spent.Id, device.LinkedByLoginCodeId);

        // GBR-02: the link is audited with the employee as the person, and without the code.
        var linked = Assert.Single(await factory.AuditAsync(worker.Id, "Employee.DeviceLinked"));
        Assert.Equal(((Guid?)worker.Id, (Guid?)null), (linked.ActorEmployeeId, linked.ActorAccountId));
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_the_fifth_wrong_entry_cancels_the_code()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var code = await IssueCodeAsync(world.Manager, worker.Id);
        var wrong = code == "000000" ? "111111" : "000000";
        var phone = factory.CreateClient();

        for (var entry = 1; entry <= 5; entry++)
        {
            var refused = await LoginWithCodeAsync(phone, worker.Phone, wrong);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("MSG-LABOR-19", await CodeOfAsync(refused));
        }

        // Even the right code is dead now.
        Assert.Equal("MSG-LABOR-19", await CodeOfAsync(await LoginWithCodeAsync(phone, worker.Phone, code)));
        var row = Assert.Single(await factory.CodesAsync(worker.Id));
        Assert.Equal(5, row.FailedAttempts);
        Assert.NotNull(row.CancelledAt);
        Assert.Null(row.ConsumedAt);
        Assert.Empty(await factory.SessionsAsync(worker.Id));
    }

    [Fact]
    public async Task Four_wrong_entries_leave_the_code_usable()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var code = await IssueCodeAsync(world.Manager, worker.Id);
        var wrong = code == "000000" ? "111111" : "000000";
        var phone = factory.CreateClient();

        for (var entry = 1; entry <= 4; entry++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginWithCodeAsync(phone, worker.Phone, wrong)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await LoginWithCodeAsync(phone, worker.Phone, code)).StatusCode);
    }

    [Fact]
    public async Task An_expired_code_is_refused_whatever_it_is()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var code = await IssueCodeAsync(world.Manager, worker.Id);
        var row = Assert.Single(await factory.CodesAsync(worker.Id));
        await factory.UpdateAsync<EmployeeLoginCode>(row.Id, c => c.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1));

        var refused = await LoginWithCodeAsync(factory.CreateClient(), worker.Phone, code);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("MSG-LABOR-19", await CodeOfAsync(refused));
        Assert.Empty(await factory.SessionsAsync(worker.Id));
    }

    [Fact]
    public async Task A_code_works_once_and_only_for_its_employee()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var other = await AddWorkerAsync(world);
        var code = await IssueCodeAsync(world.Manager, worker.Id);

        // Another employee's number with this code: wrong, and it costs the code of the other employee nothing.
        Assert.Equal("MSG-LABOR-19", await CodeOfAsync(await LoginWithCodeAsync(factory.CreateClient(), other.Phone, code)));
        Assert.Equal(HttpStatusCode.OK, (await LoginWithCodeAsync(factory.CreateClient(), worker.Phone, code)).StatusCode);

        var again = await LoginWithCodeAsync(factory.CreateClient(), worker.Phone, code);

        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Equal("MSG-LABOR-19", await CodeOfAsync(again));
        Assert.Single(await factory.SessionsAsync(worker.Id));
    }

    [Fact]
    public async Task A_login_with_a_code_but_no_valid_platform_does_not_use_the_code_up()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var code = await IssueCodeAsync(world.Manager, worker.Id);

        foreach (var platform in new[] { null, "", "windows" })
        {
            var refused = await LoginWithCodeAsync(factory.CreateClient(), worker.Phone, code, platform);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        Assert.Equal(0, Assert.Single(await factory.CodesAsync(worker.Id)).FailedAttempts);
        Assert.Equal(HttpStatusCode.OK, (await LoginWithCodeAsync(factory.CreateClient(), worker.Phone, code, "ios")).StatusCode);
    }

    // LABOR-06.1: the linked device logs in again, after a logout or an expiry, with no code.
    [Fact]
    public async Task The_linked_device_logs_in_again_without_a_code_and_a_wrong_secret_is_asked_for_one()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(factory, world, worker);

        var again = await (await LoginOnDeviceAsync(factory.CreateClient(), worker.Phone, linked)).JsonAsync(HttpStatusCode.OK);

        Assert.Equal(linked.GetProperty("deviceId").GetGuid(), again.GetProperty("deviceId").GetGuid());
        // M3: the secret is rotated at every code-less login, and the answer carries the new one.
        Assert.Matches("^[0-9a-fA-F]{64}$", again.GetProperty("deviceSecret").GetString()!);
        Assert.NotEqual(linked.GetProperty("deviceSecret").GetString(), again.GetProperty("deviceSecret").GetString());
        Assert.Equal(linked.GetProperty("qrKey").GetString(), again.GetProperty("qrKey").GetString());
        Assert.NotEqual(linked.GetProperty("refreshToken").GetString(), again.GetProperty("refreshToken").GetString());
        Assert.Equal(2, (await factory.SessionsAsync(worker.Id)).Count);
        var wrongSecret = await factory.CreateClient().PostAsJsonAsync(Login, new { phoneNumber = worker.Phone, deviceId = linked.GetProperty("deviceId").GetGuid(), deviceSecret = "f".PadLeft(64, 'f') });
        Assert.Equal("MSG-LABOR-17", await CodeOfAsync(wrongSecret));
        // The secret of one employee's device opens nobody else's.
        var other = await AddWorkerAsync(world);
        Assert.Equal("MSG-LABOR-17", await CodeOfAsync(await LoginOnDeviceAsync(factory.CreateClient(), other.Phone, linked)));
    }

    // BR-LABOR-02 and LABOR-06 step 7: the device on which a correct code is entered is the linked one; the one before is logged out.
    [Fact]
    public async Task Linking_a_new_device_logs_out_the_old_one_and_it_needs_a_code_again()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var oldDevice = await LinkDeviceAsync(factory, world, worker);
        var oldPhone = factory.AsEmployee(oldDevice);
        var secondSession = await (await LoginOnDeviceAsync(factory.CreateClient(), worker.Phone, oldDevice)).JsonAsync(HttpStatusCode.OK);
        // The code-less login ended the first session (one session per device): only the second one is open.
        Assert.Equal(HttpStatusCode.NoContent, (await factory.AsEmployee(secondSession).PutAsJsonAsync(PushToken, new { pushToken = "old-phone-token", pushPermissionGranted = true })).StatusCode);

        var newDevice = await LinkDeviceAsync(factory, world, worker);

        Assert.NotEqual(oldDevice.GetProperty("deviceId").GetGuid(), newDevice.GetProperty("deviceId").GetGuid());
        // Every session of the old device ends, its tokens stop at once, and its secret is worth nothing.
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldPhone.PostAsync(Logout, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.AsEmployee(secondSession).PostAsync(Logout, null)).StatusCode);
        var refresh = await RefreshAsync(factory.CreateClient(), oldDevice.GetProperty("refreshToken").GetString()!);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal("revoked", await ReasonAsync(refresh));
        Assert.Equal("MSG-LABOR-17", await CodeOfAsync(await LoginOnDeviceAsync(factory.CreateClient(), worker.Phone, oldDevice)));
        var old = await factory.DeviceAsync(oldDevice);
        Assert.Equal((false, EmployeeDeviceUnlinkReason.NewDevice, null), (old.IsLinked, old.UnlinkReason, old.PushToken));
        Assert.Equal(2, (await factory.SessionsAsync(worker.Id)).Count(s => s.EmployeeDeviceId == old.Id && s.RevokedAt is not null));
        // Only the new one is linked, and it works.
        Assert.Equal(1, await factory.QueryAsync(db => db.EmployeeDevices.CountAsync(d => d.EmployeeId == worker.Id && d.UnlinkedAt == null)));
        Assert.Equal(HttpStatusCode.NoContent, (await factory.AsEmployee(newDevice).PostAsync(Logout, null)).StatusCode);
        var unlinked = Assert.Single(await factory.AuditAsync(worker.Id, "Employee.DeviceUnlinked"));
        Assert.Equal(worker.Id, unlinked.ActorEmployeeId);
    }

    // M3: each code-less login replaces the secret. The one just replaced coming back is a copy of the app; one older than that is only wrong.
    [Fact]
    public async Task A_replaced_device_secret_that_comes_back_unlinks_the_device_and_ends_its_sessions()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var first = await LinkDeviceAsync(factory, world, worker);
        var second = await (await LoginOnDeviceAsync(factory.CreateClient(), worker.Phone, first)).JsonAsync(HttpStatusCode.OK);
        var third = await (await LoginOnDeviceAsync(factory.CreateClient(), worker.Phone, second)).JsonAsync(HttpStatusCode.OK);
        var stored = await factory.DeviceAsync(third);
        Assert.NotNull(stored.PreviousDeviceSecretHash);
        Assert.NotEqual(stored.DeviceSecretHash, stored.PreviousDeviceSecretHash);

        // Two secrets back is not the previous one: asked for a code, nothing unlinked.
        Assert.Equal("MSG-LABOR-17", await CodeOfAsync(await LoginOnDeviceAsync(factory.CreateClient(), worker.Phone, first)));
        Assert.True((await factory.DeviceAsync(third)).IsLinked);
        Assert.Equal(HttpStatusCode.NoContent, (await factory.AsEmployee(third).PutAsJsonAsync(PushToken, new { pushToken = (string?)null, pushPermissionGranted = false })).StatusCode);

        // The previous one is a clone: the device is unlinked, every session ends, and the current secret is worth nothing now.
        Assert.Equal("MSG-LABOR-17", await CodeOfAsync(await LoginOnDeviceAsync(factory.CreateClient(), worker.Phone, second)));

        var device = await factory.DeviceAsync(third);
        Assert.Equal((false, (EmployeeDeviceUnlinkReason?)EmployeeDeviceUnlinkReason.CloneSuspected), (device.IsLinked, device.UnlinkReason));
        Assert.All(await factory.SessionsAsync(worker.Id), s => Assert.NotNull(s.RevokedAt));
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.AsEmployee(third).PostAsync(Logout, null)).StatusCode);
        Assert.Equal("MSG-LABOR-17", await CodeOfAsync(await LoginOnDeviceAsync(factory.CreateClient(), worker.Phone, third)));
        var audited = Assert.Single(await factory.AuditAsync(worker.Id, "Employee.DeviceUnlinked"));
        Assert.Contains("copied", audited.Reason);
        // The way back is a new code, as for any unlinked device.
        var code = await IssueCodeAsync(world.Manager, worker.Id);
        Assert.Equal(HttpStatusCode.OK, (await LoginWithCodeAsync(factory.CreateClient(), worker.Phone, code)).StatusCode);
    }

    // M3: one session per device: a code-less login ends the session the device had.
    [Fact]
    public async Task A_code_less_login_ends_the_earlier_session_of_the_device()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(factory, world, worker);

        var again = await (await LoginOnDeviceAsync(factory.CreateClient(), worker.Phone, linked)).JsonAsync(HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.AsEmployee(linked).PostAsync(Logout, null)).StatusCode);
        var refresh = await RefreshAsync(factory.CreateClient(), linked.GetProperty("refreshToken").GetString()!);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal("revoked", await ReasonAsync(refresh));
        Assert.Single(await factory.SessionsAsync(worker.Id), s => s.RevokedAt is null);
        Assert.True((await factory.DeviceAsync(linked)).IsLinked);
        Assert.Equal(HttpStatusCode.NoContent, (await factory.AsEmployee(again).PostAsync(Logout, null)).StatusCode);
    }

    // M2: the 5th wrong entry leaves a trace without the code, and the issuer hears of it.
    [Fact]
    public async Task The_fifth_wrong_entry_is_audited_without_the_code_and_the_issuer_is_notified()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var code = await IssueCodeAsync(world.Manager, worker.Id);
        var wrong = code == "000000" ? "111111" : "000000";
        var phone = factory.CreateClient();
        var asNumber = $"(?<!\\d){code}(?!\\d)";
        Task<List<Notification>> TellingsAsync(Guid account) => factory.QueryAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.RecipientAccountId == account && n.MessageCode == "MSG-LABOR-19").ToListAsync());

        for (var entry = 1; entry <= 4; entry++)
        {
            await LoginWithCodeAsync(phone, worker.Phone, wrong);
        }

        Assert.Empty(await factory.AuditAsync(worker.Id, "Employee.LoginCodeCancelled"));
        Assert.Empty(await TellingsAsync(world.ManagerAccount.Id));
        await LoginWithCodeAsync(phone, worker.Phone, wrong);
        // The dead code and more wrong entries add nothing: one cancellation, one notice.
        await LoginWithCodeAsync(phone, worker.Phone, code);
        await LoginWithCodeAsync(phone, worker.Phone, wrong);

        var cancelled = Assert.Single(await factory.AuditAsync(worker.Id, "Employee.LoginCodeCancelled"));
        Assert.Equal(((Guid?)null, (Guid?)null), (cancelled.ActorAccountId, cancelled.ActorEmployeeId));
        Assert.DoesNotMatch(asNumber, $"{cancelled.Before}{cancelled.After}{cancelled.Reason}");
        var told = Assert.Single(await TellingsAsync(world.ManagerAccount.Id));
        Assert.Equal(NotificationTypes.EmployeeLoginCode, told.Type);
        Assert.Contains(worker.Name, told.Body);
        Assert.DoesNotMatch(asNumber, told.Body);
        // Only the account that issued the code is told.
        Assert.Empty(await TellingsAsync(world.OwnerAccount.Id));
    }

    // L5: an entry that cannot be a code is a bad request (MSG-GEN-01) and uses up no try.
    [Fact]
    public async Task A_malformed_entry_is_a_bad_request_and_uses_up_no_try()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var code = await IssueCodeAsync(world.Manager, worker.Id);

        foreach (var typo in new[] { code[..5], code + "0", "abcdef", "12-345", "١٢٣٤٥٦" })
        {
            var refused = await LoginWithCodeAsync(factory.CreateClient(), worker.Phone, typo);

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("MSG-GEN-01", await CodeOfAsync(refused));
        }

        Assert.Equal(0, Assert.Single(await factory.CodesAsync(worker.Id)).FailedAttempts);
        Assert.Equal(HttpStatusCode.OK, (await LoginWithCodeAsync(factory.CreateClient(), worker.Phone, code)).StatusCode);
    }
}
