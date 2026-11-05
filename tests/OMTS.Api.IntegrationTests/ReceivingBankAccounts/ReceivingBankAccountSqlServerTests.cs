using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Domain.Constants;
using OMTS.Api.IntegrationTests.Cooperatives;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileRequests;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileSqlServerTests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;
using static OMTS.Api.IntegrationTests.ReceivingBankAccounts.ReceivingBankAccountTestKit;

namespace OMTS.Api.IntegrationTests.ReceivingBankAccounts;

/// <summary>DEBT-01 on a real database: column lengths, the owned columns, row versions and parallel saves.</summary>
public sealed class ReceivingBankAccountSqlServerTests
{
    [SqlServerFact]
    public async Task The_longest_values_round_trip_and_a_replaced_account_moves_the_row_version()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var holder = Vietnamese(200);
        var number = new string('T', 19);
        var version = await ReceivingBankAccountTestKit.RowVersionAsync(owner);

        var saved = await owner.PutAsJsonAsync(Route, Body(version, number: number, holder: holder));

        Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        var stored = await QueryAsync(api, db => db.Cooperatives.AsNoTracking().Select(c => c.BankAccount).SingleAsync());
        Assert.Equal((Vietcombank, "Vietcombank", number, holder.ToUpperInvariant()), (stored!.BankBin, stored.BankName, stored.AccountNumber, stored.AccountHolderName));
        var newVersion = await ReceivingBankAccountTestKit.RowVersionAsync(owner);
        Assert.NotEqual(version, newVersion);

        var stale = await owner.PutAsJsonAsync(Route, Body(version, number: SecondNumber));
        var replaced = await owner.PutAsJsonAsync(Route, Body(newVersion, Bidv, SecondNumber));

        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(SecondNumber, (await AccountAsync(owner)).GetProperty("accountNumber").GetString());
    }

    [SqlServerFact]
    public async Task Parallel_saves_of_one_version_answer_one_success_and_conflicts_never_a_server_error()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var version = await ReceivingBankAccountTestKit.RowVersionAsync(owner);
        var numbers = Enumerable.Range(1, 8).Select(i => $"PARALLEL{i:0000}").ToArray();

        var answers = await Task.WhenAll(numbers.Select(number => owner.PutAsJsonAsync(Route, Body(version, number: number))));

        Assert.All(answers, a => Assert.True(a.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"HTTP {(int)a.StatusCode}"));
        var winner = Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        var saved = (await winner.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("receivingBankAccount").GetProperty("account").GetProperty("accountNumber").GetString();
        Assert.Equal(saved, (await AccountAsync(owner)).GetProperty("accountNumber").GetString());
        foreach (var conflict in answers.Where(a => a.StatusCode == HttpStatusCode.Conflict))
        {
            Assert.Equal("MSG06", await conflict.ErrorCodeAsync());
        }

        Assert.Equal(1, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.Action == AuditAction)));
    }

    [SqlServerFact]
    public async Task A_profile_saved_while_the_service_is_asked_makes_the_late_save_a_conflict_and_nothing_is_lost()
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
        await using var slow = WithService(api, service);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var slowOwner = slow.CreateClient();
        slowOwner.DefaultRequestHeaders.Authorization = owner.DefaultRequestHeaders.Authorization;

        var pending = slowOwner.PutAsJsonAsync(Route, Body(await ReceivingBankAccountTestKit.RowVersionAsync(owner)));
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The wait holds no lock on the row: this save goes through at once.
        var profile = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(await CooperativeProfileRequests.RowVersionAsync(owner)));
        Assert.True(profile.StatusCode == HttpStatusCode.OK, await profile.Content.ReadAsStringAsync());

        release.SetResult();
        var late = await pending;

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("MSG06", await late.ErrorCodeAsync());
        Assert.Equal(JsonValueKind.Null, (await AccountAsync(owner)).ValueKind);
        Assert.NotNull(await QueryAsync(api, db => db.Cooperatives.Select(c => c.TaxCode).SingleAsync()));
    }

    [SqlServerFact]
    public async Task The_account_and_the_profile_saved_at_once_answer_2xx_or_409_and_neither_is_half_saved()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var version = await ReceivingBankAccountTestKit.RowVersionAsync(owner);

        var answers = await Task.WhenAll(
            owner.PutAsJsonAsync(Route, Body(version)),
            owner.PutAsJsonAsync("/api/cooperative/profile", Valid(version)));

        Assert.All(answers, a => Assert.True(a.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"HTTP {(int)a.StatusCode}"));
        Assert.Contains(answers, a => a.StatusCode == HttpStatusCode.OK);
        var stored = await QueryAsync(api, db => db.Cooperatives.AsNoTracking().SingleAsync());
        Assert.Equal(answers[0].StatusCode == HttpStatusCode.OK, stored.BankAccount is not null);
        Assert.Equal(answers[1].StatusCode == HttpStatusCode.OK, stored.TaxCode is not null);
    }

    [SqlServerFact]
    public async Task The_migration_clears_an_account_that_was_never_accepted_and_reverts()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var migrations = db.Database.GetMigrations().ToList();
        var previous = migrations[migrations.FindIndex(m => m.EndsWith("_ReceivingBankAccount", StringComparison.Ordinal)) - 1];

        await migrator.MigrateAsync(previous);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO Cooperatives (Id, FullName, CreatedAt, BankAccount_BankName, BankAccount_AccountNumber, BankAccount_AccountHolderName)
            VALUES (NEWID(), N'HTX', SYSDATETIMEOFFSET(), N'BIDV', N'TEST00000001', N'HTX')
            """);
        await db.Database.MigrateAsync();

        Assert.Null((await db.Cooperatives.AsNoTracking().SingleAsync()).BankAccount);
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM Cooperatives WHERE BankAccount_AccountNumber IS NOT NULL").SingleAsync());

        await migrator.MigrateAsync(previous);
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    private static WebApplicationFactory<Program> WithService(SqlServerApiFactory api, IPaymentNotificationService service) =>
        api.WithWebHostBuilder(b => b.ConfigureTestServices(services => services.AddSingleton(service)));
}
