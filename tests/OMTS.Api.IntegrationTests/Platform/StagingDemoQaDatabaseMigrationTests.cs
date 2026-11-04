using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.Commands;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Models;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Bootstrap.Demo;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Platform.DemoOnSharedServerKit;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>QA of OMTS-216: databases with an odd migration state or an unreadable hash.</summary>
public partial class StagingDemoQaDatabaseTests
{
    // A hash that is not even Base64 stops the check with exit 1: fails closed, writes nothing.
    [Fact]
    public async Task An_unreadable_hash_in_the_database_fails_the_demo_closed()
    {
        var secret = AcsTestKit.NewPassword();
        await using var host = Host(new AuthApiFactory(), "Staging", "true", secret);
        await RunAsync(host, demo: false);
        await ChangeAsync(host, async (db, _) => (await db.Accounts.SingleAsync()).PasswordHash = "***");

        var exitCode = await BootstrapCommand.RunAsync(host.Services, ["seed", "--bootstrap", "--demo"]);

        Assert.Equal(DeploymentBootstrapper.Failure, exitCode);
        using var scope = host.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().FarmingZones.AnyAsync());
    }

    // Real migrations: a Development copy behind the migrations is refused with nothing applied.
    [SqlServerFact]
    public async Task A_development_database_behind_the_migrations_is_refused_and_no_migration_is_applied()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-qa-216-files-{Guid.NewGuid():N}");
        await using var database = SqlServerTestDatabase.Create();
        try
        {
            await using (var development = OnSqlServer(database, storageRoot, AcsTestKit.NewPassword(), "Development"))
            {
                Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(development, demo: true)).ExitCode);
            }

            int appliedBefore;
            await using (var db = database.CreateContext())
            {
                var migrations = db.Database.GetMigrations().ToList();
                await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(migrations[^2]);
                appliedBefore = (await db.Database.GetAppliedMigrationsAsync()).Count();
                Assert.Equal(migrations.Count - 1, appliedBefore);
            }

            await using var staging = OnSqlServer(database, storageRoot, AcsTestKit.NewPassword());
            var refused = await RunAsync(staging, demo: true);

            Assert.Equal(DeploymentBootstrapper.Failure, refused.ExitCode);
            Assert.Contains("well-known", Assert.Single(refused.Messages));
            await using var after = database.CreateContext();
            Assert.Equal(appliedBefore, (await after.Database.GetAppliedMigrationsAsync()).Count());
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    // The check trusts the history table to know there are no accounts. A copy without it passes the check, but the
    // migrations then stop on the existing tables: exit 1 and no demo account gets the configured password.
    [SqlServerFact]
    public async Task A_development_copy_with_its_migration_history_wiped_still_ends_in_failure_and_adds_no_demo_data()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-qa-216-files-{Guid.NewGuid():N}");
        var secret = AcsTestKit.NewPassword();
        await using var database = SqlServerTestDatabase.Create();
        try
        {
            await using (var development = OnSqlServer(database, storageRoot, secret, "Development"))
            {
                Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(development, demo: true)).ExitCode);
            }

            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                await using var wipe = connection.CreateCommand();
                wipe.CommandText = "DELETE FROM [__EFMigrationsHistory]";
                await wipe.ExecuteNonQueryAsync();
            }

            await using var staging = OnSqlServer(database, storageRoot, secret);
            Assert.NotNull(await Record.ExceptionAsync(() => RunAsync(staging, demo: true)));

            await using var db = database.CreateContext();
            var hasher = staging.Services.GetRequiredService<IPasswordHasher>();
            var accounts = await db.Accounts.AsNoTracking().ToListAsync();
            Assert.DoesNotContain(accounts, a => hasher.Verify(a.PasswordHash, secret) != PasswordCheck.Failed);
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
