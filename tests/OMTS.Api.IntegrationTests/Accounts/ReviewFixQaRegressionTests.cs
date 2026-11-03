using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>Regressions of the bugs QA found in the OMTS-198 review fixes.</summary>
public class ReviewFixQaRegressionTests
{
    // Ten parallel wrong passwords: each must answer MSG17 or MSG69 and count toward the lock, not 409 MSG06.
    [SqlServerFact]
    public async Task Parallel_wrong_current_passwords_all_answer_msg17_or_msg69_and_lock()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateAsync();
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), Password);
        var client = await LoginAsync(api, owner.Username);
        var email = $"qa-{NewCode()}@mail.com";

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Email", target = email, currentPassword = "Wrong1234" })));

        var codes = new List<string>();
        foreach (var response in responses)
        {
            codes.Add((await response.ErrorCodeAsync()) ?? "none");
        }
        var unexpected = codes.Where(c => c is not ("MSG-ACCOUNT-14" or "MSG-ACCOUNT-06")).ToList();
        Assert.True(unexpected.Count == 0, $"{unexpected.Count} of 10 answered outside MSG-ACCOUNT-14/MSG-ACCOUNT-06. All codes: {string.Join(",", codes)}");

        var stored = await database.ReadAsync(db => db.Accounts.SingleAsync(a => a.Id == owner.Id));
        Assert.True(stored.LockoutEndAt is not null, $"no lock after 10 wrong passwords; FailedLoginCount={stored.FailedLoginCount}");
    }

    // Twelve accounts start a phone verification to one new number at once: the per-number cap is 10 SMS.
    [SqlServerFact]
    public async Task Parallel_phone_verification_starts_from_many_accounts_respect_the_per_number_cap()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateAsync();
        await using var api = new SqlServerApiFactory(database);
        var victim = NewPhone();
        var clients = new List<HttpClient>();
        for (var i = 0; i < 12; i++)
        {
            var account = await api.AddAccountAsync(NewCode(), Password);
            await database.SetPhoneAsync(account.Id, NewPhone(), verified: false);
            clients.Add(await LoginAsync(api, account.Username));
        }

        var responses = await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = victim })));

        var sms = api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(victim).Count;
        var statuses = string.Join(",", responses.Select(r => (int)r.StatusCode).OrderBy(s => s));
        Assert.True(sms <= 10, $"SMS sent to one number: {sms}, cap is 10. Statuses: {statuses}");
    }

    // A VerifyPhone token issued while the phone was unverified must not overwrite a phone verified meanwhile.
    [Fact]
    public async Task Old_phone_token_cannot_overwrite_a_phone_verified_meanwhile()
    {
        await using var api = new AuthApiFactory();
        var owner = await api.AddAccountAsync(NewCode(), Password, configure: a => a.PhoneVerifiedAt = null);
        var client = await api.LoginAsync(owner.Username);
        var oldPhone = NewPhone();
        var newPhone = NewPhone();

        var oldId = await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = oldPhone }));
        var oldToken = await VerifyAsync(client, oldId, api.LastSmsCode(oldPhone));

        var contactId = await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/contact-changes",
            new { channel = "Sms", target = newPhone, currentPassword = Password }));
        var contactToken = await VerifyAsync(client, contactId, api.LastSmsCode(newPhone));
        var confirmed = await client.PostAsJsonAsync("/api/me/contact-changes/confirm", new { verificationToken = contactToken });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        var stale = await client.PostAsJsonAsync("/api/me/phone-verification/confirm", new { verificationToken = oldToken });
        var stored = await api.QueryAsync(db => db.Accounts.SingleAsync(a => a.Id == owner.Id));
        Assert.True(!stale.IsSuccessStatusCode && stored.PhoneNumber == newPhone,
            $"stale token answered HTTP {(int)stale.StatusCode}; phone now {stored.PhoneNumber}, expected {newPhone}");
    }

    private static async Task<HttpClient> LoginAsync(SqlServerApiFactory api, string username)
    {
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }
}

internal static class ReviewFixQaSqlExtensions
{
    public static async Task MigrateAsync(this SqlServerTestDatabase database)
    {
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
    }

    public static async Task<T> ReadAsync<T>(this SqlServerTestDatabase database, Func<OMTS.Infrastructure.Persistence.OmtsDbContext, Task<T>> query)
    {
        await using var db = database.CreateContext();
        return await query(db);
    }

    public static async Task SetPhoneAsync(this SqlServerTestDatabase database, Guid accountId, string phone, bool verified)
    {
        await using var db = database.CreateContext();
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
        account.PhoneNumber = phone;
        account.PhoneVerifiedAt = verified ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync();
    }
}
