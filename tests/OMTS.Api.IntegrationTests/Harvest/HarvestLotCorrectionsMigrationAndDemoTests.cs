using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>The HarvestLotCorrections migration on SQL Server: up, down and up again, with the index only on the rows it names. The demo lots after a removal.</summary>
public class HarvestLotCorrectionsMigrationAndDemoTests
{
    private const string MigrationBefore = "20261104212952_HarvestLots";
    private const string IndexName = "UX_AuditEvents_HarvestLotCreatedCode";

    private static Task<int> IndexCountAsync(OmtsDbContext db) =>
        db.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = '{IndexName}' AND is_unique = 1 AND has_filter = 1").SingleAsync();

    private static Task<string> FilterAsync(OmtsDbContext db) =>
        db.Database.SqlQueryRaw<string>($"SELECT filter_definition AS [Value] FROM sys.indexes WHERE name = '{IndexName}'").SingleAsync();

    private static AuditEvent Row(string action, string recordType, string? code = null) => new() { OccurredAt = DateTimeOffset.UtcNow, Action = action, RecordType = recordType, RecordCode = code };

    [SqlServerFact]
    public async Task The_migration_goes_up_down_and_up_again_with_the_index_only_on_the_rows_it_names()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);
        Assert.Equal(0, await IndexCountAsync(db));

        // The rows an older database holds: two rows of one code on another action, Created rows with no code, and a code with other actions beside its Created row.
        db.AuditEvents.AddRange(
            Row("Raft.Created", "Raft", "SAME"),
            Row("Raft.Created", "Raft", "SAME"),
            Row("HarvestLot.Created", "HarvestLot"),
            Row("HarvestLot.Created", "HarvestLot"),
            Row("HarvestLot.Corrected", "HarvestLot", "LOT-1"),
            Row("HarvestLot.Corrected", "HarvestLot", "LOT-1"),
            Row("HarvestLot.Created", "HarvestLot", "LOT-1"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await migrator.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(1, await IndexCountAsync(db));
        Assert.Equal("([Action]='HarvestLot.Created' AND [RecordCode] IS NOT NULL)", await FilterAsync(db));

        // Spelled as a lot code is compared: the code is unique in the cooperative.
        db.AuditEvents.Add(Row("HarvestLot.Created", "HarvestLot", "lot-1"));
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains(((SqlException)duplicate.InnerException!).Number, new[] { 2601, 2627 });
        db.ChangeTracker.Clear();

        await migrator.MigrateAsync(MigrationBefore);
        Assert.Equal(0, await IndexCountAsync(db));
        Assert.Equal(7, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM AuditEvents").SingleAsync());

        await migrator.MigrateAsync();
        Assert.Equal(1, await IndexCountAsync(db));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [SqlServerFact]
    public async Task A_demo_lot_can_be_removed_by_its_manager_and_a_rerun_neither_brings_it_back_nor_fails_on_the_index()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-demo-files-{Guid.NewGuid():N}");
        try
        {
            await using var database = SqlServerTestDatabase.Create();
            await using var api = new SqlServerApiFactory(database, new Dictionary<string, string?>
            {
                [BootstrapSettings.CooperativeFullNameKey] = "HTX QA",
                [BootstrapSettings.OwnerDisplayNameKey] = "Owner QA",
                [BootstrapSettings.OwnerUsernameKey] = "owner",
                [BootstrapSettings.OwnerPhoneNumberKey] = "0900000001",
                [BootstrapSettings.OwnerTemporaryPasswordKey] = AcsTestKit.NewPassword(),
                ["Storage:Local:RootPath"] = storageRoot,
            });
            Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(api)).ExitCode);
            var client = await LoggedInAsync(api, "quanlyvung1");
            var (lotId, raftId, top, count) = await UseDbAsync(api, async db =>
            {
                var lot = await db.HarvestLots.Where(l => l.Raft.Code == "B1-01").OrderByDescending(l => l.LotCode).Select(l => new { l.Id, l.RaftId, l.LotCode }).FirstAsync();
                return (lot.Id, lot.RaftId, lot.LotCode, await db.HarvestLots.CountAsync());
            });
            var read = await client.GetHarvestLotAsync(lotId);
            Assert.True(read.GetProperty("canRemove").GetBoolean());

            Assert.Equal(HttpStatusCode.NoContent, (await client.PostRemoveLotAsync(lotId, "Recorded by mistake", read.Text("rowVersion"))).StatusCode);

            var rerun = await RunAsync(api);
            Assert.Equal(DeploymentBootstrapper.Success, rerun.ExitCode);
            Assert.Contains(rerun.Messages, m => m == "Demo harvest lots: already there.");
            Assert.Equal(count - 1, await UseDbAsync(api, db => db.HarvestLots.CountAsync()));

            var next = (await client.RecordHarvestAsync(raftId, HarvestFields.OfLanes(1))).GetProperty("lot");
            Assert.NotEqual(top, next.Text("lotCode"));
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    /// <summary>A demo account with a password of the test's own.</summary>
    private static async Task<HttpClient> LoggedInAsync(WebApplicationFactory<Program> api, string username)
    {
        var password = AcsTestKit.NewPassword();
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
            var account = await db.Accounts.SingleAsync(a => a.Username == username);
            account.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(password);
            account.MustChangePassword = false;
            await db.SaveChangesAsync();
        }

        var client = api.CreateClient();
        var tokens = await AcsTestKit.LoginTokensAsync(client, username, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<T> UseDbAsync<T>(WebApplicationFactory<Program> api, Func<OmtsDbContext, Task<T>> query)
    {
        using var scope = api.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<OmtsDbContext>());
    }

    private static async Task<BootstrapOutcome> RunAsync(WebApplicationFactory<Program> api)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(true, CancellationToken.None);
    }
}
