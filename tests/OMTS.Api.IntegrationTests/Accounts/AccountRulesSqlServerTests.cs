using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>
/// OMTS-216 PR 0.4 on SQL Server: the AccountRulesV2 migration, the unique index on any email and the send times of a
/// verification. A parallel save that passes the check loses at the unique index: <see cref="BlindLookups"/> makes the
/// check pass, so these tests reach that branch every time, not only when a real race happens.
/// </summary>
public class AccountRulesSqlServerTests
{
    // Generated per run, so no password-like literal is committed (secret scanners).
    private static readonly string TestPassword = NewPassword();

    // Dev data only: of the accounts sharing an email, a verified one keeps it, otherwise the oldest; the new index builds.
    [SqlServerFact]
    public async Task Migration_keeps_one_holder_of_each_email_and_adds_the_index()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var all = db.Database.GetMigrations().ToList();
        await db.GetService<IMigrator>().MigrateAsync(all[all.FindIndex(m => m.EndsWith("_AccountRulesV2", StringComparison.Ordinal)) - 1]);
        var (older, verified, newer, alone, other) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await InsertAccountAsync(db, older, "older", "same@example.com", verified: false, daysOld: 3);
        await InsertAccountAsync(db, verified, "verified", "same@example.com", verified: true, daysOld: 1);
        await InsertAccountAsync(db, newer, "newer", "same@example.com", verified: false, daysOld: 2);
        await InsertAccountAsync(db, alone, "alone", "alone@example.com", verified: false, daysOld: 2);
        await InsertAccountAsync(db, other, "other1", "both@example.com", verified: false, daysOld: 2);
        await InsertAccountAsync(db, Guid.NewGuid(), "other2", "both@example.com", verified: false, daysOld: 1);

        await db.Database.MigrateAsync();

        var emails = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Username, a => a.Email);
        Assert.Equal("same@example.com", emails["verified"]);
        Assert.Null(emails["older"]);
        Assert.Null(emails["newer"]);
        Assert.Equal("alone@example.com", emails["alone"]);
        Assert.Equal("both@example.com", emails["other1"]);
        Assert.Null(emails["other2"]);

        var duplicate = () => InsertAccountAsync(db, Guid.NewGuid(), "dup", "alone@example.com", verified: false, daysOld: 0);
        await Assert.ThrowsAnyAsync<Exception>(duplicate);
    }

    // The times of the codes survive the database: a new host still counts them, and the 4th code waits for the oldest.
    [SqlServerFact]
    public async Task The_send_times_of_the_codes_are_stored_and_counted_across_requests_and_accounts()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateAsync();
        await using var api = new SqlServerApiFactory(database);
        var client = api.CreateClient();
        var phone = NewPhone();

        var first = await VerificationIdAsync(await client.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phone }));
        await AgeAsync(database, first);
        var resent = await client.PostAsync($"/api/verifications/{first}/resend", null);
        Assert.Equal(HttpStatusCode.OK, resent.StatusCode);
        await AgeAsync(database, first);
        var second = await VerificationIdAsync(await client.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phone }));
        await AgeAsync(database, second);

        var fourth = await client.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phone });
        var body = await fourth.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
        Assert.Equal("MSG-ACCOUNT-18", body.GetProperty("code").GetString());
        // The oldest code was aged twice: it was sent 4 minutes ago.
        Assert.InRange(body.GetProperty("retryAfterSeconds").GetInt32(), 3600 - 240 - 10, 3600 - 240);
        Assert.Equal(3, api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phone).Count);
        await using var db = database.CreateContext();
        Assert.Equal(2, (await db.VerificationCodes.AsNoTracking().SingleAsync(c => c.Id == first)).SendTimes.Count);
    }

    // Parallel resends and new requests of one number: never more than 3 codes for the purpose and the number.
    [SqlServerFact]
    public async Task Parallel_requests_never_send_more_than_3_codes()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateAsync();
        await using var api = new SqlServerApiFactory(database);
        var client = api.CreateClient();
        var phone = NewPhone();

        for (var round = 0; round < 6; round++)
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phone })));
            Assert.All(responses, r => Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode));
            await using var db = database.CreateContext();
            foreach (var request in await db.VerificationCodes.Where(c => c.Target == phone).ToListAsync())
            {
                request.AgeBy(TimeSpan.FromMinutes(2));
            }

            await db.SaveChangesAsync();
        }

        Assert.InRange(api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phone).Count, 1, VerificationCode.MaxCodesPerHour);
    }

    [SqlServerFact]
    public async Task A_staff_create_that_loses_the_race_answers_msg_account_09_naming_the_field()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateAsync();
        var blind = new BlindLookups();
        await using var api = BlindApi(database, blind);
        var client = await OwnerClientAsync(api);
        var email = $"race-{NewCode()}@example.com";
        var taken = await AddStaffAsync(database, api, email);

        // The check looks up the username, the phone number and, when entered, the email.
        foreach (var (field, lookups, body) in new (string, int, object)[]
        {
            ("Username", 2, new { displayName = "A", username = taken.Username, phoneNumber = NewPhone() }),
            ("Phone number", 2, new { displayName = "A", username = $"s{NewCode()}", phoneNumber = taken.PhoneNumber }),
            ("Email", 3, new { displayName = "A", username = $"s{NewCode()}", phoneNumber = NewPhone(), email }),
        })
        {
            blind.Remaining = lookups;
            var refused = await client.PostAsJsonAsync("/api/accounts", body);

            await AssertUsedByAnotherAsync(refused, field, taken.Id);
            Assert.Equal(0, blind.Remaining);
        }
    }

    [SqlServerFact]
    public async Task A_staff_edit_that_loses_the_race_answers_msg_account_09_naming_the_field()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateAsync();
        var blind = new BlindLookups();
        await using var api = BlindApi(database, blind);
        var client = await OwnerClientAsync(api);
        var taken = await AddStaffAsync(database, api, $"race-{NewCode()}@example.com");
        var edited = await AddStaffAsync(database, api, null);
        var rowVersion = (await client.GetFromJsonAsync<JsonElement>($"/api/accounts/{edited.Id}")).GetProperty("rowVersion").GetString();

        blind.Remaining = 2;
        var refused = await client.PutAsJsonAsync($"/api/accounts/{edited.Id}", new { displayName = "B", username = edited.Username, phoneNumber = taken.PhoneNumber, rowVersion });

        await AssertUsedByAnotherAsync(refused, "Phone number", taken.Id);
    }

    // BR-ACCOUNT-02 at the database: two parallel creates with one email leave one account with it.
    [SqlServerFact]
    public async Task Parallel_creates_with_one_unverified_email_create_one_account()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateAsync();
        await using var api = new SqlServerApiFactory(database);
        var client = await OwnerClientAsync(api);
        var email = $"parallel-{NewCode()}@example.com";

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            client.PostAsJsonAsync("/api/accounts", new { displayName = "A", username = $"s{NewCode()}", phoneNumber = NewPhone(), email })));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("MSG-ACCOUNT-09", await refused.ErrorCodeAsync());
        }

        Assert.DoesNotContain(api.Logs.Lines, line => line.Contains(email));
    }

    [SqlServerFact]
    public async Task An_employee_account_that_loses_the_race_answers_msg_account_09()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateAsync();
        var blind = new BlindLookups();
        await using var api = BlindApi(database, blind);
        var client = await OwnerClientAsync(api);
        var taken = await AddStaffAsync(database, api, null);
        var zone = await AddZoneAsync(database);

        blind.Remaining = 2;
        var createWithAccount = await client.PostAsJsonAsync("/api/employees", EmployeeRequests.Profile(
            $"Nhân viên {NewCode()}", NewPhone(), zone, body => body["account"] = new { username = taken.Username }));
        await AssertUsedByAnotherAsync(createWithAccount, "Username", taken.Id);

        var employee = await EmployeeRequests.CreateAsync(client, EmployeeRequests.Profile($"Nhân viên {NewCode()}", NewPhone(), zone));
        blind.Remaining = 2;
        var addAccount = await client.PostAsJsonAsync($"/api/employees/{employee.GetProperty("employee").GetProperty("id").GetGuid()}/account", new { username = taken.Username });
        await AssertUsedByAnotherAsync(addAccount, "Username", taken.Id);
    }

    [SqlServerFact]
    public async Task A_contact_change_that_loses_the_race_answers_msg_account_09()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateAsync();
        var blind = new BlindLookups();
        await using var api = BlindApi(database, blind);
        var client = await OwnerClientAsync(api);

        var phone = NewPhone();
        var phoneToken = await VerifyAsync(client, await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/contact-changes",
            new { channel = "Sms", target = phone, currentPassword = TestPassword })), SmsCode(api, phone));
        var email = $"contact-{NewCode()}@example.com";
        var emailToken = await VerifyAsync(client, await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/contact-changes",
            new { channel = "Email", target = email, currentPassword = TestPassword })), EmailCode(api, email));

        // Another account takes both between the code and the confirmation, and the check does not see it yet.
        await AddStaffAsync(database, api, email, phone);

        foreach (var (field, token) in new[] { ("Phone number", phoneToken), ("Email", emailToken) })
        {
            blind.Remaining = 1;
            var refused = await client.PostAsJsonAsync("/api/me/contact-changes/confirm", new { verificationToken = token });

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("MSG-ACCOUNT-09", body.GetProperty("code").GetString());
            Assert.Equal(field, body.GetProperty("field").GetString());
        }
    }

    [SqlServerFact]
    public async Task A_phone_step_that_loses_the_race_answers_msg_account_09()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateAsync();
        var blind = new BlindLookups();
        await using var api = BlindApi(database, blind);
        var client = await LoginAsync(api, (await AddOwnerAsync(api, phoneVerified: false)).Username);
        var phone = NewPhone();
        var token = await VerifyAsync(client, await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = phone })), SmsCode(api, phone));
        await AddStaffAsync(database, api, null, phone);

        blind.Remaining = 1;
        var refused = await client.PostAsJsonAsync("/api/me/phone-verification/confirm", new { verificationToken = token });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MSG-ACCOUNT-09", body.GetProperty("code").GetString());
        Assert.Equal("Phone number", body.GetProperty("field").GetString());
    }

    // ---- helpers ----

    /// <summary>The next <see cref="Remaining"/> uniqueness lookups find nothing, as when the other save is not committed yet.</summary>
    public sealed class BlindLookups
    {
        public int Remaining { get; set; }

        public bool Blind()
        {
            if (Remaining <= 0)
            {
                return false;
            }

            Remaining--;
            return true;
        }
    }

    private sealed class BlindAccountRepository(IAccountRepository inner, BlindLookups blind) : IAccountRepository
    {
        public Task<Account?> GetByIdAsync(Guid accountId, CancellationToken cancellationToken) => inner.GetByIdAsync(accountId, cancellationToken);

        public Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken) =>
            inner.GetDisplayNamesAsync(accountIds, cancellationToken);

        public Task<Account?> FindByLoginIdentifierAsync(string username, string? phoneNumber, string? email, CancellationToken cancellationToken) =>
            inner.FindByLoginIdentifierAsync(username, phoneNumber, email, cancellationToken);

        public Task<bool> UsernameExistsAsync(string username, Guid? exceptAccountId, CancellationToken cancellationToken) =>
            inner.UsernameExistsAsync(username, exceptAccountId, cancellationToken);

        public Task<Account?> FindByUsernameAsync(string username, CancellationToken cancellationToken) =>
            blind.Blind() ? Task.FromResult<Account?>(null) : inner.FindByUsernameAsync(username, cancellationToken);

        public Task<Account?> FindByPhoneAsync(string phoneNumber, CancellationToken cancellationToken) =>
            blind.Blind() ? Task.FromResult<Account?>(null) : inner.FindByPhoneAsync(phoneNumber, cancellationToken);

        public Task<Account?> FindByVerifiedEmailAsync(string email, CancellationToken cancellationToken) =>
            inner.FindByVerifiedEmailAsync(email, cancellationToken);

        public Task<Account?> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
            blind.Blind() ? Task.FromResult<Account?>(null) : inner.FindByEmailAsync(email, cancellationToken);

        public Task<(IReadOnlyList<Account> Items, int Total)> SearchStaffAsync(string? search, int? statusId, int page, int pageSize, CancellationToken cancellationToken) =>
            inner.SearchStaffAsync(search, statusId, page, pageSize, cancellationToken);

        public Task<IReadOnlyList<Account>> ListLinkableStaffAsync(CancellationToken cancellationToken) => inner.ListLinkableStaffAsync(cancellationToken);

        public void Add(Account account) => inner.Add(account);
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> BlindApi(SqlServerTestDatabase database, BlindLookups blind) =>
        new SqlServerApiFactory(database).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var real = services.Single(s => s.ServiceType == typeof(IAccountRepository));
            services.Remove(real);
            services.AddScoped<IAccountRepository>(provider => new BlindAccountRepository(
                (IAccountRepository)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!), blind));
        }));

    private static async Task AssertUsedByAnotherAsync(HttpResponseMessage refused, string field, Guid existingId)
    {
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MSG-ACCOUNT-09", body.GetProperty("code").GetString());
        Assert.Equal(field, body.GetProperty("field").GetString());
        Assert.Equal(existingId, body.GetProperty("existingId").GetGuid());
    }

    private static async Task<HttpClient> OwnerClientAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> api) =>
        await LoginAsync(api, (await AddOwnerAsync(api, phoneVerified: true)).Username);

    private static async Task<Account> AddOwnerAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> api, bool phoneVerified)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OMTS.Infrastructure.Persistence.OmtsDbContext>();
        var owner = new Account
        {
            Username = NewCode(),
            DisplayName = "Owner",
            PasswordHash = scope.ServiceProvider.GetRequiredService<OMTS.Application.Common.Interfaces.Identity.IPasswordHasher>().Hash(TestPassword),
            SubjectType = SubjectType.CooperativeOwner,
            PhoneNumber = NewPhone(),
            PhoneVerifiedAt = phoneVerified ? DateTimeOffset.UtcNow : null,
        };
        db.Accounts.Add(owner);
        await db.SaveChangesAsync();
        return owner;
    }

    private static async Task<HttpClient> LoginAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> api, string username)
    {
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, username, TestPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<Account> AddStaffAsync(
        SqlServerTestDatabase database, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> api, string? email, string? phone = null)
    {
        await using var db = database.CreateContext();
        var staff = new Account
        {
            Username = NewCode(),
            DisplayName = "Staff",
            PasswordHash = api.Services.GetRequiredService<OMTS.Application.Common.Interfaces.Identity.IPasswordHasher>().Hash(TestPassword),
            SubjectType = SubjectType.Staff,
            PhoneNumber = phone ?? NewPhone(),
            Email = email,
        };
        db.Accounts.Add(staff);
        await db.SaveChangesAsync();
        return staff;
    }

    private static async Task<Guid> AddZoneAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateContext();
        var zone = new FarmingZone { Code = NewCode(), Name = "Zone", Cooperative = new Cooperative { FullName = "HTX Trung Nam" } };
        db.FarmingZones.Add(zone);
        await db.SaveChangesAsync();
        return zone.Id;
    }

    private static async Task AgeAsync(SqlServerTestDatabase database, Guid id)
    {
        await using var db = database.CreateContext();
        (await db.VerificationCodes.SingleAsync(c => c.Id == id)).AgeBy(TimeSpan.FromMinutes(2));
        await db.SaveChangesAsync();
    }

    private static Task InsertAccountAsync(DbContext db, Guid id, string username, string email, bool verified, int daysOld)
    {
        var createdAt = DateTimeOffset.UtcNow.AddDays(-daysOld);
        DateTimeOffset? verifiedAt = verified ? createdAt : null;
        return db.Database.ExecuteSqlAsync(
            $"INSERT INTO Accounts (Id, Username, DisplayName, PasswordHash, SubjectType, StatusId, FailedLoginCount, MustChangePassword, CreatedAt, NotificationPreferences, Email, EmailVerifiedAt) VALUES ({id}, {username}, {username}, N'x', N'Staff', 301, 0, 0, {createdAt}, N'[]', {email}, {verifiedAt})");
    }

    private static string SmsCode(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> api, string phone) =>
        System.Text.RegularExpressions.Regex.Match(api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phone)[0].Text, @"\d{6}").Value;

    private static string EmailCode(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> api, string email) =>
        System.Text.RegularExpressions.Regex.Match(api.Services.GetRequiredService<FakeEmailSender>().Inbox.List(email)[0].Text, @"\d{6}").Value;
}
