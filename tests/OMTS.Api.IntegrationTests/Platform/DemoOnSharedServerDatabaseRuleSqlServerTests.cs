using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Bootstrap.Demo;
using OMTS.Infrastructure.Identity;
using static OMTS.Api.IntegrationTests.Platform.DemoOnSharedServerKit;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>The database rule on real migrations: a database behind them is read, never migrated, unless it is the demo's own.</summary>
public partial class DemoOnSharedServerDatabaseRuleTests
{
    /// <summary>A database stopped at the first migration, holding one account, written with the columns of that migration.</summary>
    internal static async Task CreateAtTheFirstMigrationWithAnAccountAsync(SqlServerTestDatabase database, string password)
    {
        await using (var db = database.CreateContext())
        {
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(db.Database.GetMigrations().First());
        }

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO Accounts (Id, DisplayName, Username, PasswordHash, MustChangePassword, FailedLoginCount, SubjectType, StatusId, CreatedAt) " +
            "VALUES (NEWID(), N'Real', N'real.account', @hash, 0, 0, N'Trader', @status, SYSDATETIMEOFFSET())";
        insert.Parameters.AddWithValue("@hash", new PasswordHasher().Hash(password));
        insert.Parameters.AddWithValue("@status", StatusIds.Account.Active);
        await insert.ExecuteNonQueryAsync();
    }

    // Reads only what the first migration has: an account there, in a demo run and in a plain run on a Development copy.
    [StagingDemoQaSqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task H1_a_database_at_the_first_migration_holding_accounts_is_refused_and_not_migrated(bool demo)
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-demo-files-{Guid.NewGuid():N}");
        await using var database = SqlServerTestDatabase.Create();
        await CreateAtTheFirstMigrationWithAnAccountAsync(database, demo ? AcsTestKit.NewPassword() : DemoSeedGate.WellKnownPassword);
        await using var staging = OnSqlServer(database, storageRoot, AcsTestKit.NewPassword());

        var refused = await RunAsync(staging, demo);

        Assert.Equal(DeploymentBootstrapper.Failure, refused.ExitCode);
        Assert.Contains("empty database", Assert.Single(refused.Messages));
        await using var after = database.CreateContext();
        Assert.Single(await after.Database.GetAppliedMigrationsAsync());
    }

    // The demo's own database after a new migration: its mark says it is ours, so the rerun migrates it and changes no login.
    [SqlServerFact]
    public async Task H1_the_demos_own_database_behind_the_migrations_reruns_and_is_migrated()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-demo-files-{Guid.NewGuid():N}");
        var secret = AcsTestKit.NewPassword();
        await using var database = SqlServerTestDatabase.Create();
        try
        {
            await using var staging = OnSqlServer(database, storageRoot, secret);
            Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(staging, demo: true)).ExitCode);
            await using (var db = database.CreateContext())
            {
                await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(db.Database.GetMigrations().ToList()[^2]);
            }

            var rerun = await RunAsync(staging, demo: true);

            Assert.True(rerun.ExitCode == DeploymentBootstrapper.Success, string.Join(Environment.NewLine, rerun.Messages));
            await using var after = database.CreateContext();
            Assert.Empty(await after.Database.GetPendingMigrationsAsync());
            await AssertEveryDemoLoginUsesAsync(staging, secret);
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
