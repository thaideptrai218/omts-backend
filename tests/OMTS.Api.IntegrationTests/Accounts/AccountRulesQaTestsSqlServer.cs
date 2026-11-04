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
/// A test whose comment starts with QA-n fails on purpose: it shows finding n of reports/qa-261104-account-rules.md.
/// </summary>
public class AccountRulesQaTestsSqlServer
{
    private static readonly string Pw = NewPassword();

    // Fine: a burst of wrong entries counts exactly 5, with no 500, and the right code is refused afterwards.
    [SqlServerFact]
    public async Task Parallel_wrong_entries_cancel_the_code_at_exactly_five()
    {
        await using var database = await MigratedAsync();
        await using var api = new SqlServerApiFactory(database);
        var client = api.CreateClient();
        var phone = NewPhone();
        var id = await VerificationIdAsync(await client.StartRegistrationAsync("Sms", phone));
        var code = SmsCode(api, phone);

        var answers = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => client.EnterAsync(id, WrongCodeFor(code))));

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.BadRequest, a.StatusCode));
        await using var db = database.CreateContext();
        Assert.Equal(VerificationCode.MaxFailedAttempts, (await db.VerificationCodes.AsNoTracking().SingleAsync(c => c.Id == id)).FailedAttempts);
        Assert.Equal("MSG-ACCOUNT-17", await (await client.EnterAsync(id, code)).ErrorCodeAsync());
    }

    // Fine: a right code entered in parallel gives one token.
    [SqlServerFact]
    public async Task A_right_code_entered_in_parallel_gives_one_token()
    {
        await using var database = await MigratedAsync();
        await using var api = new SqlServerApiFactory(database);
        var client = api.CreateClient();
        var phone = NewPhone();
        var id = await VerificationIdAsync(await client.StartRegistrationAsync("Sms", phone));
        var code = SmsCode(api, phone);

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.EnterAsync(id, code)));

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.All(answers.Where(a => a.StatusCode != HttpStatusCode.OK), a => Assert.Equal(HttpStatusCode.BadRequest, a.StatusCode));
    }

    // Fine: parallel starts in several spellings and parallel resends send at most one code each, with no 500.
    [SqlServerFact]
    public async Task Parallel_starts_in_several_spellings_and_parallel_resends_send_one_code()
    {
        await using var database = await MigratedAsync();
        await using var api = new SqlServerApiFactory(database);
        var client = api.CreateClient();
        var email = $"race-{NewCode()}@example.com";

        var starts = await Task.WhenAll(new[] { email, email.ToUpperInvariant(), $" {email} ", email, email.ToUpperInvariant(), email }.Select(s => client.StartRegistrationAsync("Email", s)));

        Assert.All(starts, r => Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode));
        var inbox = api.Services.GetRequiredService<FakeEmailSender>().Inbox;
        Assert.InRange(inbox.List(email).Count, 0, 1);

        var phone = NewPhone();
        var id = await VerificationIdAsync(await client.StartRegistrationAsync("Sms", phone));
        await using (var db = database.CreateContext())
        {
            (await db.VerificationCodes.SingleAsync(c => c.Id == id)).AgeBy(TwoMinutes);
            await db.SaveChangesAsync();
        }

        var resends = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.PostAsync($"/api/verifications/{id}/resend", null)));

        Assert.All(resends, r => Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode));
        Assert.InRange(api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phone).Count, 1, 2);
    }

    // Fine: two registrations of one number with two valid tokens make one account; the other answers MSG-ACCOUNT-03.
    [SqlServerFact]
    public async Task Parallel_registrations_of_one_number_make_one_account()
    {
        await using var database = await MigratedAsync();
        await using var api = new SqlServerApiFactory(database);
        await using (var db = database.CreateContext())
        {
            db.Cooperatives.Add(new Cooperative { FullName = "HTX Trung Nam" });
            await db.SaveChangesAsync();
        }

        var client = api.CreateClient();
        var phone = NewPhone();
        var tokens = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var id = await VerificationIdAsync(await client.StartRegistrationAsync("Sms", phone));
            tokens.Add(await VerifyAsync(client, id, SmsCode(api, phone)));
            await using var db = database.CreateContext();
            (await db.VerificationCodes.SingleAsync(c => c.Id == id)).AgeBy(TwoMinutes);
            await db.SaveChangesAsync();
        }

        var answers = await Task.WhenAll(tokens.Select(token => client.PostAsJsonAsync("/api/auth/register", new
        {
            channel = "Sms",
            identifier = phone,
            password = Pw,
            passwordConfirmation = Pw,
            verificationToken = token,
        })));

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.Created);
        var refused = Assert.Single(answers, a => a.StatusCode != HttpStatusCode.Created);
        Assert.Equal("MSG-ACCOUNT-03", await refused.ErrorCodeAsync());
    }

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

    // QA-M4 (BR-ACCOUNT-02, BR-ACCOUNT-09): on the real collation too: the same number in other digits gets its own codes and its own account.
    [SqlServerFact]
    public async Task A_number_in_other_digits_does_not_get_a_code_while_the_number_is_at_its_wait()
    {
        await using var database = await MigratedAsync();
        await using var api = new SqlServerApiFactory(database);
        var client = api.CreateClient();
        var phone = NewPhone();
        var arabic = new string(phone.Select((c, i) => i == 0 ? c : (char)('٠' + (c - '0'))).ToArray());
        await VerificationIdAsync(await client.StartRegistrationAsync("Sms", phone));

        var other = await client.StartRegistrationAsync("Sms", arabic);

        Assert.NotEqual(HttpStatusCode.Created, other.StatusCode);
    }

    // QA-M4 (BR-ACCOUNT-02): an account already has the number; in other digits the check passes and a code goes out.
    [SqlServerFact]
    public async Task A_taken_number_in_other_digits_answers_msg_account_03()
    {
        await using var database = await MigratedAsync();
        await using var api = new SqlServerApiFactory(database);
        await using (var db = database.CreateContext())
        {
            db.Cooperatives.Add(new Cooperative { FullName = "HTX Trung Nam" });
            await db.SaveChangesAsync();
        }

        var client = api.CreateClient();
        var phone = NewPhone();
        var existing = await api.AddAccountAsync(NewCode(), Pw, SubjectType.Trader);
        await using (var db = database.CreateContext())
        {
            (await db.Accounts.SingleAsync(a => a.Id == existing.Id)).PhoneNumber = phone;
            await db.SaveChangesAsync();
        }

        var arabic = new string(phone.Select((c, i) => i == 0 ? c : (char)(0x0660 + (c - '0'))).ToArray());
        var started = await client.StartRegistrationAsync("Sms", arabic);

        Assert.Equal("MSG-ACCOUNT-03", await started.ErrorCodeAsync());
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

    private static async Task<SqlServerTestDatabase> MigratedAsync()
    {
        var database = SqlServerTestDatabase.Create();
        await database.MigrateAsync();
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
            $"INSERT INTO Accounts (Id, Username, DisplayName, PasswordHash, SubjectType, StatusId, FailedLoginCount, MustChangePassword, CreatedAt, NotificationPreferences, Email, EmailVerifiedAt, PhoneNumber) VALUES ({id}, {username}, {username}, {hash}, N'Staff', 301, 0, 0, {createdAt}, N'[]', {email}, {verifiedAt}, {phone})");
    }

    private static string SmsCode(SqlServerApiFactory api, string phone) =>
        System.Text.RegularExpressions.Regex.Match(api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phone)[0].Text, @"\d{6}").Value;
}
