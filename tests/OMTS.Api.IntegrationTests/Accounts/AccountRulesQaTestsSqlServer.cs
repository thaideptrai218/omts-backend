using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Accounts.AccountRulesQaTestsKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>
/// Independent QA of the account rules (OMTS-216) on SQL Server: races on the limits and on uniqueness, and the migration.
/// </summary>
public class AccountRulesQaTestsSqlServer
{
    private static readonly string Pw = NewPassword();

    // Fine: two accounts confirm one number at once: one gets it, the other MSG-ACCOUNT-09 with the field, never 500.
    [SqlServerFact]
    public async Task Parallel_phone_steps_of_one_number_answer_msg_account_09()
    {
        await using var database = await MigratedAsync();
        await using var api = new SqlServerApiFactory(database);
        var phone = NewPhone();
        var clients = new List<HttpClient>();
        var tokens = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var trader = await api.AddAccountAsync(NewCode(), Pw, SubjectType.Trader);
            var client = api.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginTokensAsync(client, trader.Username, Pw)).GetProperty("accessToken").GetString());
            var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = phone }));
            tokens.Add(await VerifyAsync(client, id, SmsCode(api, phone)));
            clients.Add(client);
            await using var db = database.CreateContext();
            (await db.VerificationCodes.SingleAsync(c => c.Id == id)).AgeBy(TwoMinutes);
            await db.SaveChangesAsync();
        }

        var answers = await Task.WhenAll(clients.Select((client, i) => client.PostAsJsonAsync("/api/me/phone-verification/confirm", new { verificationToken = tokens[i] })));

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        var refused = Assert.Single(answers, a => a.StatusCode != HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("MSG-ACCOUNT-09", await refused.ErrorCodeAsync());
    }

    // BR-ACCOUNT-09 on the real rows: a change of the number by its holder cancels the recovery of the old number, the token that
    // proves the new number is used and not cancelled, and a new code cancels the earlier one of another account.
    [SqlServerFact]
    public async Task A_change_of_the_number_and_a_new_code_cancel_the_earlier_codes()
    {
        await using var database = await MigratedAsync();
        await using var api = new SqlServerApiFactory(database);
        var holder = await api.AddAccountAsync(NewCode(), Pw);
        var oldPhone = holder.PhoneNumber!;
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginTokensAsync(client, holder.Username, Pw)).GetProperty("accessToken").GetString());
        var anonymous = api.CreateClient();
        var requestId = (await (await anonymous.PostAsJsonAsync("/api/auth/recovery", new { identifier = oldPhone })).BodyAsync()).GetProperty("requestId").GetGuid();
        var recoveryToken = await VerifyAsync(anonymous, requestId, SmsCode(api, oldPhone));

        var newPhone = NewPhone();
        var changeId = await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Sms", target = newPhone, currentPassword = Pw }));
        var changeToken = await VerifyAsync(client, changeId, SmsCode(api, newPhone));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/me/contact-changes/confirm", new { verificationToken = changeToken })).StatusCode);

        var newPassword = NewPassword();
        var reset = await anonymous.PostAsJsonAsync("/api/auth/recovery/reset", new { verificationToken = recoveryToken, newPassword, passwordConfirmation = newPassword });
        Assert.Equal("MSG-ACCOUNT-16", await reset.ErrorCodeAsync());

        var contested = NewPhone();
        var (first, second) = (await TraderAsync(api), await TraderAsync(api));
        var firstId = await VerificationIdAsync(await first.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = contested }));
        var firstCode = SmsCode(api, contested);
        await using (var db = database.CreateContext())
        {
            (await db.VerificationCodes.SingleAsync(c => c.Id == firstId)).AgeBy(TwoMinutes);
            await db.SaveChangesAsync();
        }

        await VerificationIdAsync(await second.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = contested }));

        Assert.Equal("MSG-ACCOUNT-16", await (await first.EnterAsync(firstId, firstCode)).ErrorCodeAsync());
    }

    // Fine: parallel forced changes of one session never answer 500, and at most one is saved.
    [SqlServerFact]
    public async Task Parallel_forced_changes_save_one_password()
    {
        await using var database = await MigratedAsync();
        await using var api = new SqlServerApiFactory(database);
        var staff = await api.AddAccountAsync(NewCode(), Pw);
        await using (var db = database.CreateContext())
        {
            var stored = await db.Accounts.SingleAsync(a => a.Id == staff.Id);
            stored.MustChangePassword = true;
            stored.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginTokensAsync(client, staff.Username, Pw)).GetProperty("accessToken").GetString());
        var passwords = Enumerable.Range(0, 4).Select(_ => NewPassword()).ToList();

        var answers = await Task.WhenAll(passwords.Select(p => client.PostAsJsonAsync("/api/me/password", new { newPassword = p, passwordConfirmation = p })));

        Assert.All(answers, a => Assert.NotEqual(HttpStatusCode.InternalServerError, a.StatusCode));
        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
    }

    // Fine: Up with duplicates in other case keeps the verified one, leaves a single holder and the rest of the loser alone; Down
    // restores the old index and drops the column; Up runs again over new duplicates.
    [SqlServerFact]
    public async Task Migration_up_down_and_up_again()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var previous = await ToBeforeAccountRulesAsync(db);
        var email = $"mixed-{NewCode()}@example.com";
        await InsertAccountAsync(db, "older", email.ToUpperInvariant(), verified: false, daysOld: 5, phone: "0900000001");
        await InsertAccountAsync(db, "holder", email, verified: true, daysOld: 1, phone: "0900000002");
        await InsertAccountAsync(db, "single", "single@example.com", verified: false, daysOld: 3, phone: "0900000003");
        await InsertAccountAsync(db, "none", null, verified: false, daysOld: 3, phone: "0900000004");

        await db.Database.MigrateAsync();

        var up = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Username);
        Assert.Null(up["older"].Email);
        Assert.Equal("0900000001", up["older"].PhoneNumber);
        Assert.Equal(email, up["holder"].Email);
        Assert.NotNull(up["holder"].EmailVerifiedAt);
        Assert.Equal("single@example.com", up["single"].Email);
        Assert.Null(up["none"].Email);
        Assert.False(db.Database.HasPendingModelChanges());

        await db.GetService<IMigrator>().MigrateAsync(previous);

        Assert.Contains("EmailVerifiedAt", await ScalarAsync(db, "SELECT filter_definition AS Value FROM sys.indexes WHERE name = 'IX_Accounts_Email'"));
        Assert.Equal("0", await ScalarAsync(db, "SELECT CAST(COUNT(*) AS nvarchar(10)) AS Value FROM sys.columns WHERE name = 'SendTimes' AND object_id = OBJECT_ID('VerificationCodes')"));
        await InsertAccountAsync(db, "dup1", "again@example.com", verified: false, daysOld: 2, phone: "0900000005");
        await InsertAccountAsync(db, "dup2", "again@example.com", verified: false, daysOld: 1, phone: "0900000006");

        await db.Database.MigrateAsync();

        var again = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Username);
        Assert.Equal("again@example.com", again["dup1"].Email);
        Assert.Null(again["dup2"].Email);
    }

    // Fine: with no duplicate the migration changes no email.
    [SqlServerFact]
    public async Task Migration_with_no_duplicate_changes_nothing()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        await ToBeforeAccountRulesAsync(db);
        await InsertAccountAsync(db, "one", "one@example.com", verified: true, daysOld: 2, phone: "0900000001");
        await InsertAccountAsync(db, "two", "two@example.com", verified: false, daysOld: 1, phone: "0900000002");
        await InsertAccountAsync(db, "three", null, verified: false, daysOld: 1, phone: "0900000003");

        await db.Database.MigrateAsync();

        var after = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Username, a => a.Email);
        Assert.Equal("one@example.com", after["one"]);
        Assert.Equal("two@example.com", after["two"]);
        Assert.Null(after["three"]);
    }

    private static async Task<HttpClient> TraderAsync(SqlServerApiFactory api)
    {
        var trader = await api.AddAccountAsync(NewCode(), Pw, SubjectType.Trader);
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginTokensAsync(client, trader.Username, Pw)).GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<SqlServerTestDatabase> MigratedAsync()
    {
        var database = SqlServerTestDatabase.Create();
        await database.MigrateToLatestAsync();
        return database;
    }

    private static async Task<string> ToBeforeAccountRulesAsync(DbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var previous = all[all.FindIndex(m => m.EndsWith("_AccountRulesV2", StringComparison.Ordinal)) - 1];
        await db.GetService<IMigrator>().MigrateAsync(previous);
        return previous;
    }

    private static async Task<string> ScalarAsync(DbContext db, string sql) =>
        await db.Database.SqlQueryRaw<string>(sql).SingleAsync();

    private static Task InsertAccountAsync(DbContext db, string username, string? email, bool verified, int daysOld, string phone)
    {
        var createdAt = DateTimeOffset.UtcNow.AddDays(-daysOld);
        DateTimeOffset? verifiedAt = verified ? createdAt : null;
        var id = Guid.NewGuid();
        var hash = Guid.NewGuid().ToString("N");
        return db.Database.ExecuteSqlAsync(
            $"INSERT INTO Accounts (Id, Username, DisplayName, PasswordHash, SubjectType, StatusId, FailedLoginCount, MustChangePassword, CreatedAt, Email, EmailVerifiedAt, PhoneNumber) VALUES ({id}, {username}, {username}, {hash}, N'Staff', 301, 0, 0, {createdAt}, {email}, {verifiedAt}, {phone})");
    }

    private static string SmsCode(SqlServerApiFactory api, string phone) =>
        System.Text.RegularExpressions.Regex.Match(api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phone)[0].Text, @"\d{6}").Value;
}
