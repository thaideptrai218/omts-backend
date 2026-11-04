using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Bootstrap.Demo;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>QA of OMTS-216 on the real process: named tests fail on purpose; the rest record what happens around them.</summary>
public partial class StagingDemoQaRealProcessTests
{
    // L1. README: Demo:Password "comes from the secret store of that server, never from a file in the repository".
    // Nothing enforces it: appsettings.Staging.json is not ignored by .gitignore or .dockerignore, so `COPY src/ src/`
    // and `dotnet publish` carry it into the image, and the demo opens on the image alone.
    [SqlServerFact]
    public async Task L1_a_settings_file_in_the_image_does_not_open_the_demo_or_supply_its_password()
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        var secret = AcsTestKit.NewPassword();
        try
        {
            kit.WriteImageFile("appsettings.Staging.json", $$"""{ "Demo": { "AllowOutsideDevelopment": "true", "Password": "{{secret}}" } }""");

            var run = await kit.RunAsync(Settings(kit, connection, "Staging", null, null), DemoSeed);

            Assert.True(run.ExitCode == 1, "exit 0: the demo was seeded with the flag and the password from a file shipped beside the program");
        }
        finally
        {
            await StagingDemoQaProcessKit.DropDatabaseAsync(connection);
        }
    }

    // L2. The operator's ASPNETCORE_ENVIRONMENT=Production loses to a stray DOTNET_ENVIRONMENT in .NET 8's
    // WebApplication.CreateBuilder. README names only ASPNETCORE_ENVIRONMENT.
    [SqlServerFact]
    public async Task L2_a_stray_dotnet_environment_does_not_override_aspnetcore_environment_production_for_the_demo()
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        var settings = Settings(kit, connection, "Production", "true", TooShort);
        settings["DOTNET_ENVIRONMENT"] = "Staging";

        var run = await kit.RunAsync(settings, DemoSeed);

        Assert.True(run.Output.Contains("refused outside Development and Staging"), $"read as another environment: {run.Output}");
    }

    [SqlServerFact]
    public async Task L2_a_stray_dotnet_environment_does_not_open_swagger_and_the_inbox_under_aspnetcore_environment_production()
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        var settings = Settings(kit, connection, "Production", null, null);
        settings["DOTNET_ENVIRONMENT"] = "Development";
        using var api = await kit.StartApiAsync(settings);

        var swagger = await api.Client.GetAsync("/swagger/index.html");
        var inbox = await api.Client.GetAsync("/api/dev/sms");

        Assert.Equal(HttpStatusCode.NotFound, swagger.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, inbox.StatusCode);
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
    [SqlServerFact]
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
