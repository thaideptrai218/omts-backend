using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.AccessManagement;

/// <summary>
/// ACS races on real SQL Server: unique indexes, rowversion and transactions decide the outcome, which the
/// in-memory provider cannot show. Every answer must be a normal 2xx/4xx, never a 500.
/// </summary>
public class AcsSqlServerTests
{
    private const string TraderPassword = "Trader2026";

    // Double click on "Create staff": one account is created, the other requests answer MSG03.
    [SqlServerFact]
    public async Task Parallel_staff_creations_with_one_username_create_one_account()
    {
        await using var database = SqlServerTestDatabase.Create();
        await MigrateAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), Password);
        var client = await LoginAsync(api, owner.Username);
        var (zoneId, roleId) = await SeedZoneAndRoleAsync(database);
        var username = $"s{NewCode()}";

        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => client.PostAsJsonAsync("/api/accounts", StaffBody(username, zoneId, roleId))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("MSG-GEN-02", await refused.ErrorCodeAsync());
        }

        Assert.Equal(1, await ReadAsync(database, db => db.Accounts.CountAsync(a => a.Username == username)));
    }

    // Two traders register one number with two valid codes: one account, the other refused with 409, no 500.
    [SqlServerFact]
    public async Task Parallel_registrations_with_one_phone_create_one_trader()
    {
        await using var database = SqlServerTestDatabase.Create();
        await MigrateAsync(database);
        await using var api = new SqlServerApiFactory(database);
        await SeedCooperativeAsync(database);
        var phone = NewPhone();
        var anonymous = api.CreateClient();
        var first = await VerifiedTokenAsync(api, anonymous, phone);
        // The send budget allows one code per minute: age the first request by two minutes instead of waiting.
        await BackdateRequestAsync(database, first.VerificationId);
        var second = await VerifiedTokenAsync(api, anonymous, phone);

        var responses = await Task.WhenAll(new[] { first.Token, second.Token }
            .Select(token => RegisterAsync(anonymous, phone, token)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var refused = responses.Single(r => r.StatusCode != HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("MSG-ACCOUNT-03", await refused.ErrorCodeAsync());
        Assert.Equal(1, await ReadAsync(database, db => db.Accounts.CountAsync(a => a.PhoneNumber == phone)));
    }

    // Parallel "send me a code" requests of one subject (double tap, script): one SMS goes out, the others
    // back off with 429; none is a 500.
    [SqlServerFact]
    public async Task Parallel_code_requests_of_one_subject_send_one_sms()
    {
        await using var database = SqlServerTestDatabase.Create();
        await MigrateAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var phone = NewPhone();
        var anonymous = api.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => anonymous.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phone })));

        Assert.True(responses.Count(r => r.StatusCode == HttpStatusCode.Created) <= 1);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode));
        Assert.True(api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phone).Count <= 1);
        Assert.Equal(
            api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phone).Count,
            await ReadAsync(database, db => db.VerificationCodes.CountAsync(c => c.Target == phone)));
    }

    // One token, three parallel registrations: at most one consumes it; the others are refused, none is a 500.
    [SqlServerFact]
    public async Task Parallel_use_of_one_verification_token_registers_once()
    {
        await using var database = SqlServerTestDatabase.Create();
        await MigrateAsync(database);
        await using var api = new SqlServerApiFactory(database);
        await SeedCooperativeAsync(database);
        var phone = NewPhone();
        var anonymous = api.CreateClient();
        var verified = await VerifiedTokenAsync(api, anonymous, phone);

        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => RegisterAsync(anonymous, phone, verified.Token)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.InRange((int)refused.StatusCode, 400, 499);
        }

        Assert.Equal(1, await ReadAsync(database, db => db.Accounts.CountAsync(a => a.PhoneNumber == phone)));
    }

    // The same owner saves a staff account from a stale version: refused with MSG06, also when two saves race.
    [SqlServerFact]
    public async Task Stale_staff_edit_is_refused_with_msg06_at_database_level()
    {
        await using var database = SqlServerTestDatabase.Create();
        await MigrateAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), Password);
        var client = await LoginAsync(api, owner.Username);
        var (zoneId, roleId) = await SeedZoneAndRoleAsync(database);
        var username = $"s{NewCode()}";
        var phone = NewPhone();
        var created = await client.PostAsJsonAsync("/api/accounts", StaffBody(username, zoneId, roleId, phone));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("account").GetProperty("id").GetGuid();

        var original = await RowVersionAsync(client, id);
        var saved = await client.PutAsJsonAsync($"/api/accounts/{id}", EditBody("First edit", username, phone, original));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var stale = await client.PutAsJsonAsync($"/api/accounts/{id}", EditBody("Stale edit", username, phone, original));
        Assert.Equal("MSG06", await stale.ErrorCodeAsync());

        // Two saves from the same current version: the second one must fail on the row version in the database.
        var current = await RowVersionAsync(client, id);
        var racing = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(i => client.PutAsJsonAsync($"/api/accounts/{id}", EditBody($"Race {i}", username, phone, current))));

        Assert.Single(racing, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var lost in racing.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal(HttpStatusCode.Conflict, lost.StatusCode);
            Assert.Equal("MSG06", await lost.ErrorCodeAsync());
        }
    }

    private static object StaffBody(string username, Guid zoneId, Guid roleId, string? phone = null) => new
    {
        displayName = "Nguyễn Văn B",
        username,
        phoneNumber = phone ?? NewPhone(),
        grants = new[] { new { zoneId, roleId } },
    };

    private static object EditBody(string displayName, string username, string phone, string rowVersion) => new
    {
        displayName,
        username,
        phoneNumber = phone,
        email = (string?)null,
        rowVersion,
    };

    private static async Task<string> RowVersionAsync(HttpClient client, Guid id)
    {
        var account = await client.GetFromJsonAsync<JsonElement>($"/api/accounts/{id}");
        return account.GetProperty("rowVersion").GetString()!;
    }

    private static async Task<HttpResponseMessage> RegisterAsync(HttpClient anonymous, string phone, string token) =>
        await anonymous.PostAsJsonAsync("/api/auth/register", new
        {
            channel = "Sms",
            identifier = phone,
            password = TraderPassword,
            passwordConfirmation = TraderPassword,
            verificationToken = token,
        });

    /// <summary>Starts a registration for the number, reads the SMS code and verifies it, returning the token.</summary>
    private static async Task<(Guid VerificationId, string Token)> VerifiedTokenAsync(
        SqlServerApiFactory api, HttpClient anonymous, string phone)
    {
        var verificationId = await VerificationIdAsync(await anonymous.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phone }));
        var token = await VerifyAsync(anonymous, verificationId, SmsCode(api, phone));
        return (verificationId, token);
    }

    private static string SmsCode(SqlServerApiFactory api, string phone) =>
        Regex.Match(api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phone)[0].Text, @"\d{6}").Value;

    private static string WrongCodeFor(string code) => ((int.Parse(code) + 1) % 1_000_000).ToString("D6");

    /// <summary>Moves a request two minutes into the past, as if that much time had passed.</summary>
    private static async Task BackdateRequestAsync(SqlServerTestDatabase database, Guid id)
    {
        await using var db = database.CreateContext();
        var request = await db.VerificationCodes.SingleAsync(c => c.Id == id);
        request.CreatedAt -= TimeSpan.FromMinutes(2);
        request.LastSentAt -= TimeSpan.FromMinutes(2);
        await db.SaveChangesAsync();
    }

    private static async Task<HttpClient> LoginAsync(SqlServerApiFactory api, string username)
    {
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<(Guid ZoneId, Guid RoleId)> SeedZoneAndRoleAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateContext();
        var zone = new FarmingZone { Code = NewCode(), Name = "Zone", Cooperative = new Cooperative { FullName = "HTX Trung Nam" } };
        var role = new Role { Name = $"Role {NewCode()}" };
        role.Permissions.Add(new RolePermission { FunctionCode = PermissionFunctions.WaterMeasurements, CanView = true, CanCreate = true, CanEdit = false });
        db.FarmingZones.Add(zone);
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return (zone.Id, role.Id);
    }

    private static async Task SeedCooperativeAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateContext();
        db.Cooperatives.Add(new Cooperative { FullName = "HTX Trung Nam" });
        await db.SaveChangesAsync();
    }

    private static async Task MigrateAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
    }

    private static async Task<T> ReadAsync<T>(SqlServerTestDatabase database, Func<OmtsDbContext, Task<T>> query)
    {
        await using var db = database.CreateContext();
        return await query(db);
    }


    // Ten parallel wrong entries on one code: every wrong entry must count, so the request locks and the
    // right code is refused afterwards.
    [SqlServerFact]
    public async Task Parallel_wrong_entries_are_not_lost_and_lock_the_request()
    {
        await using var database = SqlServerTestDatabase.Create();
        await MigrateAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var anonymous = api.CreateClient();
        var phone = NewPhone();
        var verificationId = await VerificationIdAsync(await anonymous.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phone }));
        var code = SmsCode(api, phone);
        var wrong = WrongCodeFor(code);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => anonymous.PostAsJsonAsync($"/api/verifications/{verificationId}/verify", new { code = wrong })));

        Assert.All(responses, r => Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode));
        var persisted = await ReadAsync(database, db => db.VerificationCodes.Where(c => c.Id == verificationId).Select(c => c.FailedAttempts).SingleAsync());
        Assert.True(persisted >= VerificationCode.MaxFailedAttempts, $"only {persisted} of 10 wrong entries were saved");
        var right = await anonymous.PostAsJsonAsync($"/api/verifications/{verificationId}/verify", new { code });
        Assert.Equal(HttpStatusCode.BadRequest, right.StatusCode);
    }


    [SqlServerFact]
    public async Task Parallel_staff_creations_do_not_log_the_duplicate_username()
    {
        await using var database = SqlServerTestDatabase.Create();
        await MigrateAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), Password);
        var client = await LoginAsync(api, owner.Username);
        var (zoneId, roleId) = await SeedZoneAndRoleAsync(database);
        var username = $"s{NewCode()}";

        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => client.PostAsJsonAsync("/api/accounts", StaffBody(username, zoneId, roleId))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.DoesNotContain(api.Logs.Lines, line => line.Contains(username));
    }

    [SqlServerFact]
    public async Task Parallel_registrations_do_not_log_the_duplicate_phone()
    {
        await using var database = SqlServerTestDatabase.Create();
        await MigrateAsync(database);
        await using var api = new SqlServerApiFactory(database);
        await SeedCooperativeAsync(database);
        var phone = NewPhone();
        var anonymous = api.CreateClient();
        var first = await VerifiedTokenAsync(api, anonymous, phone);
        await BackdateRequestAsync(database, first.VerificationId);
        var second = await VerifiedTokenAsync(api, anonymous, phone);

        var responses = await Task.WhenAll(new[] { first.Token, second.Token }
            .Select(token => RegisterAsync(anonymous, phone, token)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.DoesNotContain(api.Logs.Lines, line => line.Contains(phone));
    }
}
