using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Employees.EmployeesV2QaKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>Set-up and calls of the tests of the employee app (LABOR-03, LABOR-06): a zone with its Owner and Zone Manager, workers, codes and logins.</summary>
internal static class EmployeeAppKit
{
    public const string Login = "/api/employee-app/login";
    public const string Refresh = "/api/employee-app/refresh";
    public const string Logout = "/api/employee-app/logout";
    public const string PushToken = "/api/employee-app/device/push-token";

    public sealed record World(HttpClient Owner, Account OwnerAccount, FarmingZone Zone, HttpClient Manager, Account ManagerAccount);

    public sealed record Worker(Guid Id, string Phone, string Name);

    /// <summary>A zone, its Zone Manager, and the Cooperative Owner; every password is made for the test.</summary>
    public static async Task<World> WorldAsync(AuthApiFactory factory)
    {
        await factory.EnsureCooperativeAsync();
        var password = NewPassword();
        var ownerAccount = await factory.AddAccountAsync(NewCode(), password);
        var owner = await factory.LoginAsync(ownerAccount.Username, password);
        var zone = await factory.AddZoneAsync(NewCode());
        var (manager, managerAccount) = await StaffAsync(factory, (StaffRole.ZoneManager, zone));
        return new World(owner, ownerAccount, zone, manager, managerAccount);
    }

    /// <summary>An Employed seasonal worker of the zone, with a phone number of its own unless <paramref name="withoutPhone"/>.</summary>
    public static async Task<Worker> AddWorkerAsync(World world, bool withoutPhone = false)
    {
        var (name, phone) = ($"Worker {NewCode()}", withoutPhone ? null : NewPhone());
        var id = IdOf(await CreateAsync(world.Owner, Profile(name, phone, world.Zone.Id)));
        return new Worker(id, phone ?? string.Empty, name);
    }

    /// <summary>Issues a code as <paramref name="issuer"/> (201) and returns its 6 digits.</summary>
    public static async Task<string> IssueCodeAsync(HttpClient issuer, Guid employeeId)
    {
        var issued = await (await issuer.PostAsync($"/api/employees/{employeeId}/login-codes", null)).JsonAsync(HttpStatusCode.Created);
        return issued.GetProperty("code").GetString()!;
    }

    public static Task<HttpResponseMessage> LoginWithCodeAsync(HttpClient client, string phone, string code, string? platform = "android") =>
        client.PostAsJsonAsync(Login, new { phoneNumber = phone, loginCode = code, platform, deviceName = "Test phone" });

    public static Task<HttpResponseMessage> LoginOnDeviceAsync(HttpClient client, string phone, JsonElement linked) =>
        client.PostAsJsonAsync(Login, new
        {
            phoneNumber = phone,
            deviceId = linked.GetProperty("deviceId").GetGuid(),
            deviceSecret = linked.GetProperty("deviceSecret").GetString(),
        });

    /// <summary>The Zone Manager issues a code and the worker enters it on a new phone: the tokens of the login that linked the device.</summary>
    public static async Task<JsonElement> LinkDeviceAsync(AuthApiFactory factory, World world, Worker worker)
    {
        var code = await IssueCodeAsync(world.Manager, worker.Id);
        return await (await LoginWithCodeAsync(factory.CreateClient(), worker.Phone, code)).JsonAsync(HttpStatusCode.OK);
    }

    /// <summary>A client carrying the access token of an employee app login.</summary>
    public static HttpClient AsEmployee(this AuthApiFactory factory, JsonElement tokens) => WithBearer(factory.CreateClient(), tokens.GetProperty("accessToken").GetString()!);

    public static HttpClient WithBearer(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync(Refresh, new { refreshToken });

    public static async Task<string?> ReasonAsync(HttpResponseMessage response)
    {
        await response.Content.LoadIntoBufferAsync();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return body.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
    }

    public static Task<EmployeeDevice> DeviceAsync(this AuthApiFactory factory, JsonElement tokens) =>
        factory.QueryAsync(db => db.EmployeeDevices.AsNoTracking().SingleAsync(d => d.Id == tokens.GetProperty("deviceId").GetGuid()));

    public static Task<List<EmployeeSession>> SessionsAsync(this AuthApiFactory factory, Guid employeeId) =>
        factory.QueryAsync(db => db.EmployeeSessions.AsNoTracking().Where(s => s.EmployeeId == employeeId).ToListAsync());

    public static Task<List<EmployeeLoginCode>> CodesAsync(this AuthApiFactory factory, Guid employeeId) =>
        factory.QueryAsync(db => db.EmployeeLoginCodes.AsNoTracking().Where(c => c.EmployeeId == employeeId).OrderBy(c => c.IssuedAt).ToListAsync());

    public static Task<List<AuditEvent>> AuditAsync(this AuthApiFactory factory, Guid employeeId, string action) =>
        factory.QueryAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.RecordId == employeeId && e.Action == action).ToListAsync());
}
