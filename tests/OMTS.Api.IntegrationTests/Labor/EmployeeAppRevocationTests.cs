using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Employees.EmployeesV2QaKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.EmployeeAppKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>BR-LABOR-02: what logs an employee out of the employee app and unlinks the device (a phone number change, the end of the employment) and what does not.</summary>
public class EmployeeAppRevocationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static async Task<(JsonElement Saved, Worker Worker)> CreateWorkerAsync(World world)
    {
        var (name, phone) = ($"Worker {NewCode()}", NewPhone());
        var saved = await CreateAsync(world.Owner, Profile(name, phone, world.Zone.Id));
        return (saved, new Worker(IdOf(saved), phone, name));
    }

    private async Task AssertLoggedOutAsync(JsonElement linked, Guid employeeId, EmployeeDeviceUnlinkReason reason)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.AsEmployee(linked).PostAsync(Logout, null)).StatusCode);
        var refresh = await RefreshAsync(factory.CreateClient(), linked.GetProperty("refreshToken").GetString()!);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal("revoked", await ReasonAsync(refresh));
        var device = await factory.DeviceAsync(linked);
        Assert.Equal((false, (EmployeeDeviceUnlinkReason?)reason), (device.IsLinked, device.UnlinkReason));
        Assert.All(await factory.SessionsAsync(employeeId), s => Assert.NotNull(s.RevokedAt));
    }

    // LABOR-01.1 and BR-LABOR-02: a changed phone number logs the employee out, and the device is unlinked, so the new number needs a new code.
    [Fact]
    public async Task A_changed_phone_number_logs_the_employee_out_and_the_new_number_needs_a_new_code()
    {
        var world = await WorldAsync(factory);
        var (saved, worker) = await CreateWorkerAsync(world);
        var linked = await LinkDeviceAsync(factory, world, worker);

        // The same number in another spelling and another name change nothing.
        var renamed = await (await world.Owner.PutAsJsonAsync($"/api/employees/{worker.Id}",
            Profile($"{worker.Name} II", $"+84 {worker.Phone[1..]}", world.Zone.Id).With("rowVersion", RowVersionOf(saved)))).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await factory.AsEmployee(linked).PostAsync(Logout, null)).StatusCode);
        Assert.True((await factory.DeviceAsync(linked)).IsLinked);
        var again = await (await LoginOnDeviceAsync(factory.CreateClient(), worker.Phone, linked)).JsonAsync(HttpStatusCode.OK);

        var newPhone = NewPhone();
        var changed = await world.Owner.PutAsJsonAsync($"/api/employees/{worker.Id}",
            Profile($"{worker.Name} II", newPhone, world.Zone.Id).With("rowVersion", renamed.GetProperty("employee").GetProperty("rowVersion").GetString()));
        await changed.JsonAsync(HttpStatusCode.OK);

        await AssertLoggedOutAsync(again, worker.Id, EmployeeDeviceUnlinkReason.PhoneChanged);
        Assert.Equal("MSG-LABOR-18", await (await LoginOnDeviceAsync(factory.CreateClient(), worker.Phone, linked)).ErrorCodeAsync());
        var unlinked = Assert.Single(await factory.AuditAsync(worker.Id, "Employee.DeviceUnlinked"));
        Assert.Equal(world.OwnerAccount.Id, unlinked.ActorAccountId);
        // The old secret on the new number: the device is unlinked, so a code is asked for, and with one the new number links a device again.
        Assert.Equal("MSG-LABOR-17", await (await LoginOnDeviceAsync(factory.CreateClient(), newPhone, linked)).ErrorCodeAsync());
        var code = await IssueCodeAsync(world.Manager, worker.Id);
        Assert.Equal(HttpStatusCode.OK, (await LoginWithCodeAsync(factory.CreateClient(), newPhone, code)).StatusCode);
    }

    [Fact]
    public async Task Clearing_the_phone_number_logs_the_employee_out_too()
    {
        var world = await WorldAsync(factory);
        var (saved, worker) = await CreateWorkerAsync(world);
        var linked = await LinkDeviceAsync(factory, world, worker);

        await (await world.Owner.PutAsJsonAsync($"/api/employees/{worker.Id}", Profile(worker.Name, null, world.Zone.Id).With("rowVersion", RowVersionOf(saved)))).JsonAsync(HttpStatusCode.OK);

        await AssertLoggedOutAsync(linked, worker.Id, EmployeeDeviceUnlinkReason.PhoneChanged);
    }

    // An employee who never used the app: a phone number change has nothing to end, and writes no unlink to the audit log.
    [Fact]
    public async Task A_phone_number_change_of_an_employee_without_a_device_ends_nothing()
    {
        var world = await WorldAsync(factory);
        var (saved, worker) = await CreateWorkerAsync(world);

        await (await world.Owner.PutAsJsonAsync($"/api/employees/{worker.Id}", Profile(worker.Name, NewPhone(), world.Zone.Id).With("rowVersion", RowVersionOf(saved)))).JsonAsync(HttpStatusCode.OK);

        Assert.Empty(await factory.AuditAsync(worker.Id, "Employee.DeviceUnlinked"));
    }

    [Fact]
    public async Task An_end_of_employment_before_today_logs_out_at_once_and_unlinks_the_device()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(factory, world, worker);

        await EndAsync(world.Manager, worker.Id);

        await AssertLoggedOutAsync(linked, worker.Id, EmployeeDeviceUnlinkReason.EmploymentEnded);
        Assert.Equal("MSG-LABOR-18", await (await LoginOnDeviceAsync(factory.CreateClient(), worker.Phone, linked)).ErrorCodeAsync());
        Assert.Equal("MSG-LABOR-09", await (await world.Manager.PostAsync($"/api/employees/{worker.Id}/login-codes", null)).ErrorCodeAsync());
        var unlinked = Assert.Single(await factory.AuditAsync(worker.Id, "Employee.DeviceUnlinked"));
        Assert.Equal(world.ManagerAccount.Id, unlinked.ActorAccountId);
    }

    // BR-LABOR-02: "from the day after the last working day, the employee is logged out and the device is unlinked".
    [Fact]
    public async Task A_last_working_day_of_today_logs_out_the_next_day_by_the_job_with_the_system_as_the_person()
    {
        using var host = new AuthApiFactory();
        var world = await WorldAsync(host);
        var worker = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(host, world, worker);
        await EndAsync(world.Owner, worker.Id, Today());
        var phone = host.AsEmployee(linked);
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PutAsJsonAsync(PushToken, new { pushToken = "still-employed", pushPermissionGranted = true })).StatusCode);

        Assert.Equal(1, await host.Services.GetRequiredService<EmploymentEndJob>().RunOnceAsync(Today().AddDays(1)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.PostAsync(Logout, null)).StatusCode);
        var device = await host.DeviceAsync(linked);
        Assert.Equal((false, (EmployeeDeviceUnlinkReason?)EmployeeDeviceUnlinkReason.EmploymentEnded, null), (device.IsLinked, device.UnlinkReason, device.PushToken));
        Assert.All(await host.SessionsAsync(worker.Id), s => Assert.NotNull(s.RevokedAt));
        var unlinked = Assert.Single(await host.AuditAsync(worker.Id, "Employee.DeviceUnlinked"));
        Assert.Equal(((Guid?)null, (Guid?)null), (unlinked.ActorAccountId, unlinked.ActorEmployeeId));
    }

    // The job runs at 00:01 and a missed run is made up: in between, the day after the last working day, the app already refuses.
    [Fact]
    public async Task The_day_after_the_last_working_day_the_app_refuses_before_the_job_has_run()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(factory, world, worker);
        await factory.UpdateAsync<Employee>(worker.Id, e => e.LastWorkingDay = Today().AddDays(-1));

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.AsEmployee(linked).PostAsync(Logout, null)).StatusCode);
        var refresh = await RefreshAsync(factory.CreateClient(), linked.GetProperty("refreshToken").GetString()!);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal("revoked", await ReasonAsync(refresh));
        Assert.Equal("MSG-LABOR-18", await (await LoginOnDeviceAsync(factory.CreateClient(), worker.Phone, linked)).ErrorCodeAsync());
        Assert.Equal(StatusIds.Employee.Working, await factory.QueryAsync(db => db.Employees.Where(e => e.Id == worker.Id).Select(e => e.StatusId).SingleAsync()));
    }

    // LABOR-01.4 POST-3: a resumed employee comes back with the device unlinked: a new code is needed.
    [Fact]
    public async Task A_resumed_employee_needs_a_new_login_code()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(factory, world, worker);
        await EndAsync(world.Owner, worker.Id);

        var resumed = await world.Owner.PostAsJsonAsync($"/api/employees/{worker.Id}/resume", new { rowVersion = await CurrentVersionAsync(world.Owner, worker.Id) });
        await resumed.JsonAsync(HttpStatusCode.OK);

        Assert.Equal("MSG-LABOR-17", await (await LoginOnDeviceAsync(factory.CreateClient(), worker.Phone, linked)).ErrorCodeAsync());
        var code = await IssueCodeAsync(world.Manager, worker.Id);
        Assert.Equal(HttpStatusCode.OK, (await LoginWithCodeAsync(factory.CreateClient(), worker.Phone, code)).StatusCode);
    }

    // Employees of other people are untouched: ending one employee never ends another's session.
    [Fact]
    public async Task Ending_one_employee_leaves_the_session_of_another_alone()
    {
        var world = await WorldAsync(factory);
        var leaving = await AddWorkerAsync(world);
        var staying = await AddWorkerAsync(world);
        await LinkDeviceAsync(factory, world, leaving);
        var stays = await LinkDeviceAsync(factory, world, staying);

        await EndAsync(world.Owner, leaving.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await factory.AsEmployee(stays).PostAsync(Logout, null)).StatusCode);
        Assert.True((await factory.DeviceAsync(stays)).IsLinked);
    }
}
