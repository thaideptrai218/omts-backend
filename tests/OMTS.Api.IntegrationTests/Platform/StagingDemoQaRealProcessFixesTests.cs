using Microsoft.Data.SqlClient;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>QA of OMTS-216 on the real process: the demo's database rule (H1) and a plain bootstrap on a Development copy (M1).</summary>
public partial class StagingDemoQaRealProcessTests
{
    private static async Task<int> CountAsync(string connectionString, string table)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM [{table}]";
        return (int)(await command.ExecuteScalarAsync())!;
    }

    [SqlServerFact]
    public async Task H1_on_the_real_process_the_demo_runs_on_an_empty_database_and_its_own_and_refuses_any_other()
    {
        using var kit = new StagingDemoQaProcessKit();
        var own = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        var other = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        var secret = AcsTestKit.NewPassword();
        try
        {
            var settings = Settings(kit, own, "Staging", "true", secret);
            Assert.Equal(0, (await kit.RunAsync(settings, DemoSeed)).ExitCode);
            Assert.Equal(0, (await kit.RunAsync(settings, DemoSeed)).ExitCode);

            var handover = Settings(kit, other, "Production", null, null);
            Assert.Equal(0, (await kit.RunAsync(handover, "seed", "--bootstrap")).ExitCode);
            var auditRows = await CountAsync(other, "AuditEvents");
            handover[EnvironmentVariable] = "Staging";
            handover[FlagVariable] = "true";
            handover[PasswordVariable] = secret;

            var refused = await kit.RunAsync(handover, DemoSeed);

            Assert.Equal(1, refused.ExitCode);
            Assert.Contains("only runs on an empty database or on its own", refused.Output);
            Assert.DoesNotContain(secret, refused.Output);
            Assert.Equal(auditRows, await CountAsync(other, "AuditEvents"));
            Assert.Equal(1, await CountAsync(other, "Accounts"));
        }
        finally
        {
            await StagingDemoQaProcessKit.DropDatabaseAsync(own);
            await StagingDemoQaProcessKit.DropDatabaseAsync(other);
        }
    }

    [SqlServerFact]
    public async Task H1_on_the_real_process_a_database_behind_the_migrations_holding_accounts_is_refused_and_not_migrated()
    {
        using var kit = new StagingDemoQaProcessKit();
        await using var database = SqlServerTestDatabase.Create();
        await DemoOnSharedServerDatabaseRuleTests.CreateAtTheFirstMigrationWithAnAccountAsync(database, AcsTestKit.NewPassword());

        var refused = await kit.RunAsync(Settings(kit, database.ConnectionString, "Staging", "true", AcsTestKit.NewPassword()), DemoSeed);

        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("only runs on an empty database or on its own", refused.Output);
        Assert.Equal(1, await CountAsync(database.ConnectionString, "__EFMigrationsHistory"));
        Assert.Equal(1, await CountAsync(database.ConnectionString, "Accounts"));
    }

    [StagingDemoQaSqlServerTheory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task M1_on_the_real_process_a_plain_bootstrap_refuses_a_development_copy(string environment)
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        try
        {
            var settings = kit.ServerSettings(connection);
            settings[EnvironmentVariable] = "Development";
            Assert.Equal(0, (await kit.RunAsync(settings, DemoSeed)).ExitCode);
            var auditRows = await CountAsync(connection, "AuditEvents");
            settings[EnvironmentVariable] = environment;

            var plain = await kit.RunAsync(settings, "seed", "--bootstrap");

            Assert.Equal(1, plain.ExitCode);
            Assert.Contains("hold the well-known password", plain.Output);
            Assert.Contains("owner", plain.Output);
            Assert.DoesNotContain(OMTS.Infrastructure.Bootstrap.Demo.DemoSeedGate.WellKnownPassword, plain.Output);
            Assert.Equal(auditRows, await CountAsync(connection, "AuditEvents"));
        }
        finally
        {
            await StagingDemoQaProcessKit.DropDatabaseAsync(connection);
        }
    }
}
