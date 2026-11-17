using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Labor;
using OMTS.Infrastructure.Persistence.Configurations.Labor;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;
using static OMTS.Api.IntegrationTests.Labor.EmployeeAppKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// BR-LABOR-02 on a real SQL Server (the filtered unique indexes, the row versions and the retries only it shows): one open code and one linked
/// device per employee, one use of a code and at most 5 wrong entries, whatever runs in parallel. 2xx or 4xx, never 500.
/// </summary>
public class EmployeeAppSqlServerTests
{
    private static async Task<(SqlServerApiFactory Api, HttpClient Owner, Guid ZoneId)> StartOwnerAsync(SqlServerTestDatabase database)
    {
        var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        return (api, owner, (await AddZoneAsync(api, cooperative.Id)).Id);
    }

    private static async Task<(Guid Id, string Phone)> NewWorkerAsync(HttpClient owner, Guid zoneId)
    {
        var phone = NewPhone();
        return (IdOf(await CreateAsync(owner, Profile($"Worker {NewCode()}", phone, zoneId))), phone);
    }

    private static async Task AssertNoServerErrorAsync(IEnumerable<HttpResponseMessage> responses)
    {
        foreach (var response in responses)
        {
            await response.Content.LoadIntoBufferAsync();
            Assert.True((int)response.StatusCode < 500, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    private static Task<HttpResponseMessage> EnterCodeAsync(HttpClient client, string phone, string code) => LoginWithCodeAsync(client, phone, code);

    // The migration has the two filtered unique indexes the model names, and the database refuses a second open code or a second linked device.
    [SqlServerFact]
    public async Task The_database_allows_one_open_code_and_one_linked_device_per_employee()
    {
        await using var database = SqlServerTestDatabase.Create();
        var (api, owner, zoneId) = await StartOwnerAsync(database);
        await using var host = api;
        var (employeeId, _) = await NewWorkerAsync(owner, zoneId);
        var issuer = await api.AddAccountAsync(NewCode(), NewPassword());
        var indexes = await QueryAsync(api, db => db.Database.SqlQueryRaw<string>(
            "SELECT i.name AS [Value] FROM sys.indexes i WHERE i.is_unique = 1 AND i.filter_definition IS NOT NULL").ToListAsync());
        Assert.Contains(EmployeeAppConfigurations.OneOpenLoginCodePerEmployee, indexes);
        Assert.Contains(EmployeeAppConfigurations.OneLinkedDevicePerEmployee, indexes);

        EmployeeLoginCode NewCodeRow() => new() { EmployeeId = employeeId, IssuedByAccountId = issuer.Id, CodeHash = "h", IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        var spent = NewCodeRow();
        spent.ConsumedAt = DateTimeOffset.UtcNow;
        await ChangeAsync(api, db => db.EmployeeLoginCodes.Add(spent));
        await ChangeAsync(api, db => db.EmployeeLoginCodes.Add(NewCodeRow()));

        var secondOpen = await Assert.ThrowsAsync<DbUpdateException>(() => ChangeAsync(api, db => db.EmployeeLoginCodes.Add(NewCodeRow())));
        Assert.IsType<SqlException>(secondOpen.InnerException);
        Assert.Contains(((SqlException)secondOpen.InnerException!).Number, new[] { 2601, 2627 });

        EmployeeDevice NewDevice() => new() { EmployeeId = employeeId, DeviceSecretHash = "h", Platform = "android", LinkedAt = DateTimeOffset.UtcNow, LinkedByLoginCodeId = spent.Id, LastSeenAt = DateTimeOffset.UtcNow };
        var oldDevice = NewDevice();
        oldDevice.UnlinkedAt = DateTimeOffset.UtcNow;
        await ChangeAsync(api, db => db.EmployeeDevices.Add(oldDevice));
        await ChangeAsync(api, db => db.EmployeeDevices.Add(NewDevice()));
        var secondLinked = await Assert.ThrowsAsync<DbUpdateException>(() => ChangeAsync(api, db => db.EmployeeDevices.Add(NewDevice())));
        Assert.IsType<SqlException>(secondLinked.InnerException);
    }

    // Parallel issuers leave one open code, every answer is 201 or 4xx, and exactly one of the codes handed out still logs in.
    [SqlServerFact]
    public async Task Parallel_issuers_leave_exactly_one_open_code_and_one_that_works()
    {
        await using var database = SqlServerTestDatabase.Create();
        var (api, owner, zoneId) = await StartOwnerAsync(database);
        await using var host = api;

        for (var round = 0; round < 3; round++)
        {
            var (employeeId, phone) = await NewWorkerAsync(owner, zoneId);

            var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => owner.PostAsync($"/api/employees/{employeeId}/login-codes", null)));

            await AssertNoServerErrorAsync(responses);
            var created = responses.Where(r => r.StatusCode == HttpStatusCode.Created).ToList();
            Assert.NotEmpty(created);
            var codes = new List<string>();
            foreach (var response in created)
            {
                codes.Add((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!);
            }

            var rows = await QueryAsync(api, db => db.EmployeeLoginCodes.AsNoTracking().Where(c => c.EmployeeId == employeeId).ToListAsync());
            Assert.Equal(created.Count, rows.Count);
            Assert.Equal(1, rows.Count(c => c.ConsumedAt == null && c.CancelledAt == null));
            // At most 3 of the 4 are wrong, under the 5 tries of the open code.
            var phoneClient = api.CreateClient();
            var worked = 0;
            foreach (var code in codes.Distinct())
            {
                worked += (await EnterCodeAsync(phoneClient, phone, code)).StatusCode == HttpStatusCode.OK ? 1 : 0;
            }

            Assert.Equal(1, worked);
        }
    }

    // One code, many phones at once: one login, one linked device, one session, and nobody else gets in or sees a 500.
    [SqlServerFact]
    public async Task Parallel_logins_with_one_code_link_one_device_and_use_the_code_once()
    {
        await using var database = SqlServerTestDatabase.Create();
        var (api, owner, zoneId) = await StartOwnerAsync(database);
        await using var host = api;

        for (var round = 0; round < 3; round++)
        {
            var (employeeId, phone) = await NewWorkerAsync(owner, zoneId);
            var code = await IssueCodeAsync(owner, employeeId);

            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => EnterCodeAsync(api.CreateClient(), phone, code)));

            await AssertNoServerErrorAsync(responses);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, loser.StatusCode);
                Assert.Equal("MSG-LABOR-19", await loser.ErrorCodeAsync());
            }

            Assert.Equal(1, await QueryAsync(api, db => db.EmployeeDevices.CountAsync(d => d.EmployeeId == employeeId && d.UnlinkedAt == null)));
            Assert.Equal(1, await QueryAsync(api, db => db.EmployeeSessions.CountAsync(s => s.EmployeeId == employeeId)));
            Assert.Equal(1, await QueryAsync(api, db => db.EmployeeLoginCodes.CountAsync(c => c.EmployeeId == employeeId && c.ConsumedAt != null)));
        }
    }

    // The 5 tries hold under parallel wrong entries: 12 at once count 5, cancel the code, and the right code is dead afterwards.
    [SqlServerFact]
    public async Task Parallel_wrong_entries_cancel_the_code_at_exactly_five_and_the_right_code_is_dead()
    {
        await using var database = SqlServerTestDatabase.Create();
        var (api, owner, zoneId) = await StartOwnerAsync(database);
        await using var host = api;
        var (employeeId, phone) = await NewWorkerAsync(owner, zoneId);
        var code = await IssueCodeAsync(owner, employeeId);
        var wrong = code == "000000" ? "111111" : "000000";

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => EnterCodeAsync(api.CreateClient(), phone, wrong)));

        await AssertNoServerErrorAsync(responses);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        var row = await QueryAsync(api, db => db.EmployeeLoginCodes.AsNoTracking().SingleAsync(c => c.EmployeeId == employeeId));
        Assert.Equal(5, row.FailedAttempts);
        Assert.NotNull(row.CancelledAt);
        Assert.Equal("MSG-LABOR-19", await (await EnterCodeAsync(api.CreateClient(), phone, code)).ErrorCodeAsync());
        Assert.Equal(0, await QueryAsync(api, db => db.EmployeeSessions.CountAsync(s => s.EmployeeId == employeeId)));
        // M2: the cancellation is audited once and the issuer told once, however many entries ran in parallel.
        Assert.Equal(1, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.RecordId == employeeId && e.Action == "Employee.LoginCodeCancelled")));
        Assert.Equal(1, await QueryAsync(api, db => db.Notifications.CountAsync(n => n.MessageCode == "MSG-LABOR-19" && n.Type == "EMPLOYEE_LOGIN_CODE")));
    }

    // M3: the same device secret presented by several requests at once: only one is the next secret in line, the others are a copy.
    // One login, no 500, and afterwards the device is unlinked and no session is open.
    [SqlServerFact]
    public async Task Parallel_code_less_logins_with_one_secret_leave_one_login_and_an_unlinked_device()
    {
        await using var database = SqlServerTestDatabase.Create();
        var (api, owner, zoneId) = await StartOwnerAsync(database);
        await using var host = api;
        var (employeeId, phone) = await NewWorkerAsync(owner, zoneId);
        var linkResponse = await EnterCodeAsync(api.CreateClient(), phone, await IssueCodeAsync(owner, employeeId));
        Assert.Equal(HttpStatusCode.OK, linkResponse.StatusCode);
        var linked = await linkResponse.Content.ReadFromJsonAsync<JsonElement>();
        var (deviceId, secret) = (linked.GetProperty("deviceId").GetGuid(), linked.GetProperty("deviceSecret").GetString());

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            api.CreateClient().PostAsJsonAsync(Login, new { phoneNumber = phone, deviceId, deviceSecret = secret })));

        await AssertNoServerErrorAsync(responses);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, loser.StatusCode);
            Assert.Equal("MSG-LABOR-17", await loser.ErrorCodeAsync());
        }

        var device = await QueryAsync(api, db => db.EmployeeDevices.AsNoTracking().SingleAsync(d => d.Id == deviceId));
        Assert.False(device.IsLinked);
        Assert.Equal(OMTS.Domain.Enums.EmployeeDeviceUnlinkReason.CloneSuspected, device.UnlinkReason);
        Assert.Equal(0, await QueryAsync(api, db => db.EmployeeSessions.CountAsync(s => s.EmployeeId == employeeId && s.RevokedAt == null)));
    }
}
