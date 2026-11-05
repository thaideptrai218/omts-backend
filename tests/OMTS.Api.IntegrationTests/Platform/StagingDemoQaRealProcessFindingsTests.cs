using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Bootstrap.Demo;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>QA of OMTS-216 on the real process: tests named after a finding, and what happens around them.</summary>
public partial class StagingDemoQaRealProcessTests
{
    // L1, as decided: such a file stays out of the repository and of the image (`COPY src/ src/` honours .dockerignore).
    // One put beside the program by hand is still read: Which_configuration_source_... shows it, and the README says so.
    [Theory]
    [InlineData("appsettings.Staging.json")]
    [InlineData("appsettings.Production.json")]
    public void L1_a_server_settings_file_stays_out_of_the_repository_and_the_image(string file)
    {
        var root = StagingDemoQaRepositoryFiles.Root();

        Assert.Contains($"**/{file}", File.ReadAllLines(Path.Combine(root, ".gitignore")));
        Assert.Contains($"**/{file}", File.ReadAllLines(Path.Combine(root, ".dockerignore")));
    }

    // L2, as decided: the operator's ASPNETCORE_ENVIRONMENT=Production loses to a stray DOTNET_ENVIRONMENT in .NET 8's
    // WebApplication.CreateBuilder, so two different names stop the program before the host reads either.
    [SqlServerFact]
    public async Task L2_a_stray_dotnet_environment_does_not_override_aspnetcore_environment_production_for_the_demo()
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        var settings = Settings(kit, connection, "Production", "true", TooShort);
        settings["DOTNET_ENVIRONMENT"] = "Staging";

        var run = await kit.RunAsync(settings, DemoSeed);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Refused to start", run.Output);
        Assert.Contains("DOTNET_ENVIRONMENT", run.Output);
        Assert.DoesNotContain("Demo:Password", run.Output);
        Assert.False(await StagingDemoQaProcessKit.DatabaseExistsAsync(connection));
    }

    [SqlServerFact]
    public async Task L2_a_stray_dotnet_environment_does_not_open_swagger_and_the_inbox_under_aspnetcore_environment_production()
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        var settings = Settings(kit, connection, "Production", null, null);
        settings["DOTNET_ENVIRONMENT"] = "Development";

        using var api = await kit.StartApiAsync(settings);

        Assert.True(api.HasExited, "the API started with two different environment names");
        Assert.Equal(1, api.ExitCode);
    }

    // The same environment in another letter case is no conflict: .NET compares the names without regard to case.
    [SqlServerFact]
    public async Task L2_the_same_name_in_another_letter_case_is_not_a_conflict()
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        var settings = Settings(kit, connection, "Production", "true", AcsTestKit.NewPassword());
        settings["DOTNET_ENVIRONMENT"] = "PRODUCTION";

        var run = await kit.RunAsync(settings, DemoSeed);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("refused outside Development and Staging", run.Output);
    }

    // The command line wins over the variable: an operator's explicit choice, same as before this branch.
    [SqlServerFact]
    public async Task The_command_line_names_the_environment_and_production_there_beats_a_staging_variable()
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();

        var run = await kit.RunAsync(Settings(kit, connection, "Staging", "true", AcsTestKit.NewPassword()), [.. DemoSeed, "--environment", "Production"]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("refused outside Development and Staging", run.Output);
        Assert.False(await StagingDemoQaProcessKit.DatabaseExistsAsync(connection));
    }

    // Spelling variants of the word --demo seed nothing as a demo, and --demo without the seed word starts no seed.
    [SqlServerFact]
    public async Task A_mistyped_demo_word_never_seeds_demo_data()
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        try
        {
            var settings = Settings(kit, connection, "Staging", "true", AcsTestKit.NewPassword());

            var run = await kit.RunAsync(settings, "seed", "--bootstrap", "--DEMO");

            Assert.Equal(0, run.ExitCode);
            Assert.DoesNotContain("Demo ", run.Output);
            Assert.DoesNotContain("Demo mode", run.Output);
        }
        finally
        {
            await StagingDemoQaProcessKit.DropDatabaseAsync(connection);
        }
    }

    // A failure talking to the database goes to the console: neither the demo secret nor the database password with it.
    [SqlServerFact]
    public async Task An_unreachable_database_fails_with_exit_1_and_prints_no_secret()
    {
        using var kit = new StagingDemoQaProcessKit();
        var secret = AcsTestKit.NewPassword();
        var databasePassword = AcsTestKit.NewPassword();
        var connection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = "127.0.0.1,1",
            InitialCatalog = "omts_none",
            UserID = "qa",
            Password = databasePassword,
            TrustServerCertificate = true,
            ConnectTimeout = 2,
        }.ConnectionString;

        var run = await kit.RunAsync(Settings(kit, connection, "Staging", "true", secret), DemoSeed);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Bootstrap failed", run.Output);
        Assert.DoesNotContain(secret, run.Output);
        Assert.DoesNotContain(databasePassword, run.Output);
    }

    // The gate's refusals write nothing; a later failure of the same command is different. Missing Bootstrap keys come
    // after the migrations, exactly as before this branch. Recorded: not a demo refusal.
    [SqlServerFact]
    public async Task Missing_bootstrap_keys_after_a_good_gate_exit_1_with_migrations_applied_as_before_this_branch()
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        try
        {
            var settings = Settings(kit, connection, "Staging", "true", AcsTestKit.NewPassword());
            settings[StagingDemoQaProcessKit.EnvKey("Bootstrap:Owner:Username")] = null;

            var run = await kit.RunAsync(settings, DemoSeed);

            Assert.Equal(1, run.ExitCode);
            Assert.Contains("Missing bootstrap secrets", run.Output);
            Assert.True(await StagingDemoQaProcessKit.DatabaseExistsAsync(connection));
        }
        finally
        {
            await StagingDemoQaProcessKit.DropDatabaseAsync(connection);
        }
    }

    // Staging seed: its start-up check does not run, as the change list says. The API does run it, and stops.
    [SqlServerFact]
    public async Task Fake_ocr_stops_the_real_api_in_staging_and_production_but_not_the_seed_command()
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        try
        {
            var settings = Settings(kit, connection, "Staging", null, null);
            settings["Ocr__Provider"] = "Fake";

            var seed = await kit.RunAsync(settings, "seed", "--bootstrap");
            var staging = await kit.RunAsync(settings);
            settings[EnvironmentVariable] = "Production";
            var production = await kit.RunAsync(settings);

            Assert.Equal(0, seed.ExitCode);
            Assert.NotEqual(0, staging.ExitCode);
            Assert.Contains("Ocr:Provider=Fake", staging.Output);
            Assert.NotEqual(0, production.ExitCode);
            Assert.Contains("Ocr:Provider=Fake", production.Output);
        }
        finally
        {
            await StagingDemoQaProcessKit.DropDatabaseAsync(connection);
        }
    }

    // L5. .env.example: "for your own machine only". Nothing refuses its signing keys elsewhere. Not new on this branch.
    [SqlServerFact(Skip = "Waiting for the PM: should the API refuse the signing keys committed in .env.example outside Development?")]
    public async Task L5_the_signing_keys_printed_in_env_example_are_refused_outside_development()
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        var values = StagingDemoQaRepositoryFiles.EnvExampleValues();
        var settings = Settings(kit, connection, "Staging", null, null);
        settings["Jwt__SigningKey"] = values["JWT_SIGNING_KEY"];
        settings["Storage__Local__SigningKey"] = values["STORAGE_SIGNING_KEY"];

        using var api = await kit.StartApiAsync(settings);

        Assert.True(api.HasExited, "The API started and answers in Staging with the signing keys committed in .env.example");
    }
}
