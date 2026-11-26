using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Bootstrap.Demo;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Platform.DemoOnSharedServerKit;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>Staging refuses a database whose accounts hold the well-known password; other databases pass the check.</summary>
public class DemoWellKnownPasswordDatabaseTests
{
    [Fact]
    public async Task A_database_seeded_in_development_is_refused_in_staging_and_stays_unchanged()
    {
        var secret = AcsTestKit.NewPassword();
        var factory = new AuthApiFactory();
        await using var development = Host(factory, "Development", "false", null);
        Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(development, demo: true)).ExitCode);
        var before = await SnapshotAsync(development);
        await using var staging = Host(factory, "Staging", "true", secret);

        var refused = await RunAsync(staging, demo: true);

        AssertRefusedWithoutAnyPassword(refused, secret);
        Assert.Equal(before, await SnapshotAsync(staging));
    }

    [SqlServerFact]
    public async Task On_sql_server_a_database_seeded_in_development_is_refused_in_staging_and_stays_unchanged()
    {
        var secret = AcsTestKit.NewPassword();
        await OnSqlServerDatabaseAsync(async (database, storageRoot) =>
        {
            await using var development = OnSqlServer(database, storageRoot, secret, "Development");
            Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(development, demo: true)).ExitCode);
            var before = await SnapshotAsync(development);
            await using var staging = OnSqlServer(database, storageRoot, secret);

            var refused = await RunAsync(staging, demo: true);

            AssertRefusedWithoutAnyPassword(refused, secret);
            Assert.Equal(before, await SnapshotAsync(staging));
        });
    }

    [Fact]
    public async Task A_database_seeded_in_staging_with_a_secret_reruns_with_exit_0_and_stays_unchanged()
    {
        var secret = AcsTestKit.NewPassword();
        await using var staging = Host(new AuthApiFactory(), "Staging", "true", secret);
        Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(staging, demo: true)).ExitCode);
        var before = await SnapshotAsync(staging);

        var rerun = await RunAsync(staging, demo: true);

        Assert.Equal(DeploymentBootstrapper.Success, rerun.ExitCode);
        Assert.Equal(before, await SnapshotAsync(staging));
    }

    // Each account alone is enough, with a fresh salt, so only a real verify finds it: a hash comparison would not.
    [Fact]
    public async Task Any_single_account_holding_the_well_known_password_is_enough_to_refuse()
    {
        var secret = AcsTestKit.NewPassword();
        await using var staging = Host(new AuthApiFactory(), "Staging", "true", secret);
        await RunAsync(staging, demo: true);
        var usernames = (await SnapshotAsync(staging)).Accounts.Split('\n').Select(a => a.Split('|')[0]).ToList();
        Assert.Equal(11, usernames.Count);

        foreach (var username in usernames)
        {
            var original = await SetHashAsync(staging, username, hasher => hasher.Hash(DemoSeedGate.WellKnownPassword));

            var refused = await RunAsync(staging, demo: true);

            AssertRefusedWithoutAnyPassword(refused, secret);
            Assert.Contains(username, Assert.Single(refused.Messages));
            await SetHashAsync(staging, username, _ => original);
        }

        Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(staging, demo: true)).ExitCode);
    }

    // The check runs before the migrations, so it must cope with what the README asks for: an empty database.
    [SqlServerFact]
    public async Task An_empty_database_that_already_exists_passes_the_check_and_gets_the_demo_data()
    {
        var secret = AcsTestKit.NewPassword();
        await OnSqlServerDatabaseAsync(async (database, storageRoot) =>
        {
            var builder = new SqlConnectionStringBuilder(database.ConnectionString);
            var name = builder.InitialCatalog;
            builder.InitialCatalog = "master";
            await using (var connection = new SqlConnection(builder.ConnectionString))
            {
                await connection.OpenAsync();
                await using var create = connection.CreateCommand();
                create.CommandText = $"CREATE DATABASE [{name}]";
                await create.ExecuteNonQueryAsync();
            }

            await using var staging = OnSqlServer(database, storageRoot, secret);

            Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(staging, demo: true)).ExitCode);
            await AssertEveryDemoLoginUsesAsync(staging, secret);
        });
    }

    // And with a database behind the current migrations: the check reads only columns the first migration has.
    [SqlServerFact]
    public async Task A_database_behind_the_migrations_passes_the_check_and_is_migrated()
    {
        var secret = AcsTestKit.NewPassword();
        await OnSqlServerDatabaseAsync(async (database, storageRoot) =>
        {
            await using (var db = database.CreateContext())
            {
                await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(db.Database.GetMigrations().First());
            }

            await using var staging = OnSqlServer(database, storageRoot, secret);

            Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(staging, demo: true)).ExitCode);
            await AssertEveryDemoLoginUsesAsync(staging, secret);
        });
    }

    private static void AssertRefusedWithoutAnyPassword(BootstrapOutcome refused, string secret)
    {
        Assert.Equal(DeploymentBootstrapper.Failure, refused.ExitCode);
        var message = Assert.Single(refused.Messages);
        Assert.Contains("empty database", message);
        Assert.DoesNotContain(DemoSeedGate.WellKnownPassword, message);
        Assert.DoesNotContain(secret, message);
    }

    private sealed record Snapshot(string Accounts, string Counts, string Migrations);

    /// <summary>Every account's login state, the row counts of the tables a run would write to, and the applied migrations.</summary>
    private static async Task<Snapshot> SnapshotAsync(WebApplicationFactory<Program> api)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.Username).ToListAsync();
        var counts = string.Join(",",
            await db.Cooperatives.CountAsync(), await db.Employees.CountAsync(), await db.FarmingZones.CountAsync(),
            await db.PurchaseReceipts.CountAsync(), await db.WaterMeasurements.CountAsync(), await db.AuditEvents.CountAsync());
        var migrations = db.Database.IsRelational() ? string.Join(",", await db.Database.GetAppliedMigrationsAsync()) : string.Empty;
        return new Snapshot(
            string.Join("\n", accounts.Select(a => $"{a.Username}|{a.PasswordHash}|{a.MustChangePassword}|{a.PhoneVerifiedAt:O}|{a.UpdatedAt:O}")),
            counts,
            migrations);
    }

    /// <summary>Replaces the hash of one account and returns the previous one.</summary>
    private static async Task<string> SetHashAsync(WebApplicationFactory<Program> api, string username, Func<IPasswordHasher, string> hash)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var account = await db.Accounts.SingleAsync(a => a.Username == username);
        var previous = account.PasswordHash;
        account.PasswordHash = hash(scope.ServiceProvider.GetRequiredService<IPasswordHasher>());
        await db.SaveChangesAsync();
        return previous;
    }

    private static async Task OnSqlServerDatabaseAsync(Func<SqlServerTestDatabase, string, Task> test)
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-demo-files-{Guid.NewGuid():N}");
        await using var database = SqlServerTestDatabase.Create();
        try
        {
            await test(database, storageRoot);
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }
}
