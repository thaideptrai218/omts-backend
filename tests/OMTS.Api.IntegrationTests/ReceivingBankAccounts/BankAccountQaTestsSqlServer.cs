using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;
using static OMTS.Api.IntegrationTests.ReceivingBankAccounts.ReceivingBankAccountTestKit;

namespace OMTS.Api.IntegrationTests.ReceivingBankAccounts;

/// <summary>QA of DEBT-01 on a real SQL Server: races with other saves, also during the wait on the service, and failed saves.</summary>
public sealed class BankAccountQaTestsSqlServer : IDisposable
{
    private const string ProfileRoute = "/api/cooperative/profile";

    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"omts-qa-bank-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_storage))
        {
            Directory.Delete(_storage, recursive: true);
        }
    }

    /// <summary>As the kit, with logos stored in a folder of this test, not below the API project.</summary>
    private async Task<SqlServerApiFactory> StartAsync(SqlServerTestDatabase database)
    {
        await using (var db = database.CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        return new SqlServerApiFactory(database, new Dictionary<string, string?> { ["Storage:Local:RootPath"] = _storage });
    }

    [SqlServerFact]
    public async Task The_version_a_save_returns_is_the_current_one_for_the_first_and_a_replaced_account()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);

        var first = await owner.PutAsJsonAsync(Route, Body(await ReceivingBankAccountTestKit.RowVersionAsync(owner)));
        var firstVersion = await SavedVersionAsync(first);
        Assert.Equal(firstVersion, await ReceivingBankAccountTestKit.RowVersionAsync(owner));

        var replaced = await owner.PutAsJsonAsync(Route, Body(firstVersion, Bidv, SecondNumber, "HTX KHAC"));
        var replacedVersion = await SavedVersionAsync(replaced);
        Assert.Equal(replacedVersion, await ReceivingBankAccountTestKit.RowVersionAsync(owner));

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(ProfileRoute, Valid(replacedVersion))).StatusCode);
    }

    [SqlServerFact]
    public async Task A_replaced_account_changes_every_account_column_and_no_other_field_of_the_cooperative()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(ProfileRoute, Valid(await ReceivingBankAccountTestKit.RowVersionAsync(owner)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UploadLogoAsync(owner, Png, "logo.png")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(Route, Body(await ReceivingBankAccountTestKit.RowVersionAsync(owner)))).StatusCode);
        var before = await QueryAsync(api, db => db.Cooperatives.AsNoTracking().SingleAsync());

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(Route, Body(await ReceivingBankAccountTestKit.RowVersionAsync(owner), Bidv, SecondNumber, "HTX KHAC"))).StatusCode);

        var after = await QueryAsync(api, db => db.Cooperatives.AsNoTracking().SingleAsync());
        Assert.Equal((Bidv, "BIDV", SecondNumber, "HTX KHAC"), (after.BankAccount!.BankBin, after.BankAccount.BankName, after.BankAccount.AccountNumber, after.BankAccount.AccountHolderName));
        Assert.True(after.BankAccount.AcceptedAt > before.BankAccount!.AcceptedAt);
        Assert.Equal(
            (before.FullName, before.TaxCode, before.OfficeAddress, before.PhoneNumber, before.Email, before.Website, before.LegalRepresentative, before.BusinessLines, before.LogoUrl),
            (after.FullName, after.TaxCode, after.OfficeAddress, after.PhoneNumber, after.Email, after.Website, after.LegalRepresentative, after.BusinessLines, after.LogoUrl));
    }

    [SqlServerFact]
    public async Task Parallel_replacements_of_an_account_answer_one_success_and_conflicts()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        await owner.PutAsJsonAsync(Route, Body(await ReceivingBankAccountTestKit.RowVersionAsync(owner)));
        var version = await ReceivingBankAccountTestKit.RowVersionAsync(owner);

        var answers = await Task.WhenAll(Enumerable.Range(1, 8).Select(i => owner.PutAsJsonAsync(Route, Body(version, number: $"QAPARALLEL{i:00}"))));

        Assert.All(answers, a => Assert.True(a.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"HTTP {(int)a.StatusCode}"));
        var winner = Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        var saved = (await winner.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("receivingBankAccount").GetProperty("account").GetProperty("accountNumber").GetString();
        Assert.Equal(saved, (await AccountAsync(owner)).GetProperty("accountNumber").GetString());
        Assert.Equal(2, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.Action == AuditAction)));
    }

    [SqlServerFact]
    public async Task Many_rounds_of_account_profile_and_logo_at_once_never_answer_a_server_error_and_lose_nothing()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);

        for (var round = 0; round < 10; round++)
        {
            var version = await ReceivingBankAccountTestKit.RowVersionAsync(owner);
            var number = $"QAROUND{round:0000}";
            var name = $"HTX vòng {round}";
            var answers = await Task.WhenAll(
                owner.PutAsJsonAsync(Route, Body(version, number: number)),
                owner.PutAsJsonAsync(Route, Body(version, number: number + "X")),
                owner.PutAsJsonAsync(ProfileRoute, Valid(version, b => b["fullName"] = name)),
                UploadLogoAsync(owner, Png, "logo.png"));

            Assert.All(answers, a => Assert.True(a.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"HTTP {(int)a.StatusCode}"));
            var stored = await QueryAsync(api, db => db.Cooperatives.AsNoTracking().SingleAsync());
            if (answers[0].StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal(number, stored.BankAccount!.AccountNumber);
            }
            else if (answers[1].StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal(number + "X", stored.BankAccount!.AccountNumber);
            }

            if (answers[2].StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal(name, stored.FullName);
            }
        }
    }

    [SqlServerFact]
    public async Task During_the_wait_nothing_is_locked_and_a_late_save_keeps_the_old_account_and_the_other_saves()
    {
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ScriptedPaymentService(async (_, _) =>
        {
            asked.TrySetResult();
            await release.Task;
            return AccountAcceptanceResult.Accepted;
        });
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        await using var slow = WithPort(api, service);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        await owner.PutAsJsonAsync(Route, Body(await ReceivingBankAccountTestKit.RowVersionAsync(owner)));
        var old = (await AccountAsync(owner)).GetRawText();
        var slowOwner = slow.CreateClient();
        slowOwner.DefaultRequestHeaders.Authorization = owner.DefaultRequestHeaders.Authorization;

        var pending = slowOwner.PutAsJsonAsync(Route, Body(await ReceivingBankAccountTestKit.RowVersionAsync(owner), Bidv, SecondNumber));
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // No session of this database holds a transaction or a lock on a row while the service is asked. The background jobs of
        // the test hosts read this database too, so one sight of a lock proves nothing: the request holds its own until the
        // release below, so it could never reach zero here.
        await AssertNoneHeldDuringTheWaitAsync(api, "open transactions",
            "SELECT COUNT(*) AS [Value] FROM sys.dm_exec_sessions WHERE database_id = DB_ID() AND open_transaction_count > 0 AND session_id <> @@SPID");
        await AssertNoneHeldDuringTheWaitAsync(api, "locks",
            "SELECT COUNT(*) AS [Value] FROM sys.dm_tran_locks WHERE resource_database_id = DB_ID() AND resource_type <> 'DATABASE' AND request_session_id <> @@SPID");

        // Reads, a profile save and a logo all go through at once.
        Assert.Equal(old, (await AccountAsync(owner)).GetRawText());
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(ProfileRoute, Valid(await ReceivingBankAccountTestKit.RowVersionAsync(owner), b => b["fullName"] = "HTX trong lúc chờ"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UploadLogoAsync(owner, Png, "logo.png")).StatusCode);
        var stored = await QueryAsync(api, db => db.Cooperatives.AsNoTracking().SingleAsync());

        release.SetResult();
        var late = await pending;

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("MSG06", await late.ErrorCodeAsync());
        Assert.Equal(old, (await AccountAsync(owner)).GetRawText());
        var after = await QueryAsync(api, db => db.Cooperatives.AsNoTracking().SingleAsync());
        Assert.Equal(("HTX trong lúc chờ", stored.LogoUrl, stored.TaxCode), (after.FullName, after.LogoUrl, after.TaxCode));
        Assert.Equal(1, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.Action == AuditAction)));
        Assert.DoesNotContain(SecondNumber, string.Join('\n', api.Logs.Lines));
    }

    private static async Task AssertNoneHeldDuringTheWaitAsync(SqlServerApiFactory api, string what, string countSql)
    {
        var count = -1;
        for (var attempt = 0; attempt < 75 && count != 0; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }

            count = await QueryAsync(api, db => db.Database.SqlQueryRaw<int>(countSql).SingleAsync());
        }

        var holders = count == 0 ? string.Empty : string.Join("; ", await QueryAsync(api, db => db.Database.SqlQueryRaw<string>(
            "SELECT CONCAT(l.request_session_id, ' ', l.resource_type, ' ', l.request_mode, ' ', s.program_name) AS [Value] FROM sys.dm_tran_locks l " +
            "JOIN sys.dm_exec_sessions s ON s.session_id = l.request_session_id WHERE l.resource_database_id = DB_ID() AND l.resource_type <> 'DATABASE' AND l.request_session_id <> @@SPID").ToListAsync()));
        Assert.True(count == 0, $"{count} {what} held for the whole wait. {holders}");
    }

    // The client list, row "The record changed since it was read": "409 MSG06, with currentRowVersion. Also when another save
    // of the cooperative lands while the service answers." NFR-09: the new version is shown next to the one being entered.
    [SqlServerFact]
    public async Task M1_a_save_overtaken_during_the_wait_answers_msg06_with_the_current_version_and_who_saved()
    {
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ScriptedPaymentService(async (_, _) =>
        {
            asked.TrySetResult();
            await release.Task;
            return AccountAcceptanceResult.Accepted;
        });
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        await using var slow = WithPort(api, service);
        var (owner, account, _) = await OwnerWithCooperativeAsync(api);
        var slowOwner = slow.CreateClient();
        slowOwner.DefaultRequestHeaders.Authorization = owner.DefaultRequestHeaders.Authorization;

        var pending = slowOwner.PutAsJsonAsync(Route, Body(await ReceivingBankAccountTestKit.RowVersionAsync(owner)));
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(ProfileRoute, Valid(await ReceivingBankAccountTestKit.RowVersionAsync(owner)))).StatusCode);
        release.SetResult();
        var problem = await ProblemAsync(await pending);

        Assert.Equal("MSG06", problem.GetProperty("code").GetString());
        Assert.True(problem.TryGetProperty("currentRowVersion", out _), problem.GetRawText());
        Assert.Contains(account.DisplayName, problem.GetProperty("detail").GetString());
    }

    // The reverse: the logo has no version of its own and takes a while to store. The account saved meanwhile stays.
    [SqlServerFact]
    public async Task A_logo_being_stored_while_the_account_is_saved_answers_a_conflict_and_the_account_stays()
    {
        var storing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        await using var slowStore = api.WithWebHostBuilder(b => b.ConfigureTestServices(services => services.AddSingleton<IFileStorage>(new SlowStorage(storing, release))));
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var slowOwner = slowStore.CreateClient();
        slowOwner.DefaultRequestHeaders.Authorization = owner.DefaultRequestHeaders.Authorization;

        var logo = UploadLogoAsync(slowOwner, Png, "logo.png");
        await storing.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(Route, Body(await ReceivingBankAccountTestKit.RowVersionAsync(owner)))).StatusCode);
        release.SetResult();
        var answer = await logo;

        Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
        Assert.Equal("MSG06", await answer.ErrorCodeAsync());
        Assert.Equal(FirstNumber, (await AccountAsync(owner)).GetProperty("accountNumber").GetString());
    }

    [SqlServerFact]
    public async Task A_profile_form_read_before_the_account_was_saved_cannot_touch_the_account()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var form = await ReceivingBankAccountTestKit.RowVersionAsync(owner);
        await owner.PutAsJsonAsync(Route, Body(await ReceivingBankAccountTestKit.RowVersionAsync(owner)));
        var saved = (await AccountAsync(owner)).GetRawText();

        var stale = await owner.PutAsJsonAsync(ProfileRoute, Valid(form));
        var fresh = await owner.PutAsJsonAsync(ProfileRoute, Valid(await ReceivingBankAccountTestKit.RowVersionAsync(owner)));

        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(saved, (await AccountAsync(owner)).GetRawText());
    }

    // A save that fails after the service accepted: a real failure may answer 500, but the old account stays and nothing leaks.
    [SqlServerFact]
    public async Task A_database_failure_after_the_acceptance_keeps_the_old_account_and_keeps_the_number_out_of_the_answer_and_the_log()
    {
        const string blocked = "QAFAILSAVE01";
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        await owner.PutAsJsonAsync(Route, Body(await ReceivingBankAccountTestKit.RowVersionAsync(owner)));
        var old = (await AccountAsync(owner)).GetRawText();
        await QueryAsync(api, db => db.Database.ExecuteSqlRawAsync(
            $"ALTER TABLE Cooperatives ADD CONSTRAINT CK_Qa_Blocked CHECK (BankAccount_AccountNumber IS NULL OR BankAccount_AccountNumber <> '{blocked}')"));

        var failed = await owner.PutAsJsonAsync(Route, Body(await ReceivingBankAccountTestKit.RowVersionAsync(owner), number: blocked));
        var body = await failed.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.DoesNotContain(blocked, body);
        Assert.DoesNotContain("CK_Qa_Blocked", body);
        Assert.Equal(old, (await AccountAsync(owner)).GetRawText());
        Assert.Equal(1, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.Action == AuditAction)));
        Assert.DoesNotContain(blocked, string.Join('\n', api.Logs.Lines));
    }

    [SqlServerFact]
    public async Task Surrogate_pairs_at_the_limit_fit_the_column()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var holder = string.Concat(Enumerable.Repeat("𝓗", 100));

        var saved = await owner.PutAsJsonAsync(Route, Body(await ReceivingBankAccountTestKit.RowVersionAsync(owner), holder: holder));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(holder, (await QueryAsync(api, db => db.Cooperatives.AsNoTracking().Select(c => c.BankAccount).SingleAsync()))!.AccountHolderName);
    }

    private static async Task<string> SavedVersionAsync(HttpResponseMessage saved)
    {
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("receivingBankAccount").GetProperty("rowVersion").GetString()!;
    }

    private static WebApplicationFactory<Program> WithPort(SqlServerApiFactory api, IPaymentNotificationService service) =>
        api.WithWebHostBuilder(b => b.ConfigureTestServices(services => services.AddSingleton(service)));

    /// <summary>A store that only answers when told to; the logo tests need no real file.</summary>
    private sealed class SlowStorage(TaskCompletionSource storing, TaskCompletionSource release) : IFileStorage
    {
        public async Task<FileUploadResult> UploadAsync(ValidatedFile file, string category, CancellationToken cancellationToken = default)
        {
            storing.TrySetResult();
            await release.Task;
            return FileUploadResult.Stored($"{category}/2026/11/{Guid.NewGuid():N}.png");
        }

        public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(null);

        public Task<string> GetReadUrlAsync(string key, TimeSpan? lifetime = null, CancellationToken cancellationToken = default) => Task.FromResult(key);

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyList<StoredFile>> ListAsync(string category, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredFile>>([]);
    }
}
