using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Bootstrap.Demo;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>QA of OMTS-216 on the real process: which configuration source opens the demo.</summary>
public partial class StagingDemoQaRealProcessTests
{
    [SqlServerFact]
    public async Task Which_configuration_source_can_open_the_flag_and_user_secrets_or_the_development_file_cannot()
    {
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        var flagJson = """{ "Demo": { "AllowOutsideDevelopment": "true" } }""";
        var wrong = new List<string>();

        async Task Check(string source, bool opens, Action<StagingDemoQaProcessKit, Dictionary<string, string?>> arrange, params string[] arguments)
        {
            using var kit = new StagingDemoQaProcessKit();
            var settings = Settings(kit, connection, "Staging", null, TooShort);
            arrange(kit, settings);
            var run = await kit.RunAsync(settings, [.. DemoSeed, .. arguments]);
            var opened = run.Output.Contains("Demo:Password must have");
            if (opened != opens || run.ExitCode != 1)
            {
                wrong.Add($"{source}: expected opens={opens}, exit {run.ExitCode}: {run.Output}");
            }
        }

        await Check("no source", opens: false, (_, _) => { });
        await Check("environment variable, double underscore", opens: true, (_, s) => s[FlagVariable] = "true");
        await Check("environment variable, colon", opens: true, (_, s) => s["Demo:AllowOutsideDevelopment"] = "true");
        await Check("command line, --key=value", opens: true, (_, _) => { }, "--Demo:AllowOutsideDevelopment=true");
        await Check("command line, /key=value", opens: true, (_, _) => { }, "/Demo:AllowOutsideDevelopment=true");
        await Check("command line, key=value", opens: true, (_, _) => { }, "Demo:AllowOutsideDevelopment=true");
        await Check("user secrets", opens: false, (kit, _) => kit.WriteUserSecrets(flagJson));
        await Check("appsettings.Development.json", opens: false, (kit, _) => kit.WriteImageFile("appsettings.Development.json", flagJson));
        await Check(".env beside the program", opens: false, (kit, _) => kit.WriteImageFile(".env", "Demo__AllowOutsideDevelopment=true"));
        await Check("appsettings.Production.json", opens: false, (kit, _) => kit.WriteImageFile("appsettings.Production.json", flagJson));
        // These two are finding L1: shown here as what the code does today.
        await Check("appsettings.json", opens: true, (kit, _) => kit.WriteImageFile("appsettings.json", flagJson));
        await Check("appsettings.Staging.json", opens: true, (kit, _) => kit.WriteImageFile("appsettings.Staging.json", flagJson));

        Assert.Empty(wrong);
        Assert.False(await StagingDemoQaProcessKit.DatabaseExistsAsync(connection));
    }

    // Control of the test above: the secrets file is really where the host looks, in Development.
    [SqlServerFact]
    public async Task The_user_secrets_file_of_the_kit_is_read_in_development_so_its_refusal_in_staging_means_something()
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        try
        {
            var settings = kit.ServerSettings(connection);
            settings[EnvironmentVariable] = "Development";
            settings[StagingDemoQaProcessKit.EnvKey("Bootstrap:Cooperative:FullName")] = null;
            kit.WriteUserSecrets("""{ "Bootstrap": { "Cooperative": { "FullName": "From user secrets" } } }""");

            var run = await kit.RunAsync(settings, "seed", "--bootstrap");

            Assert.Equal(0, run.ExitCode);
            Assert.Contains("Cooperative created", run.Output);
        }
        finally
        {
            await StagingDemoQaProcessKit.DropDatabaseAsync(connection);
        }
    }

    // A setting file cannot name the environment: the host reads it from the variable or the command line only.
    [SqlServerFact]
    public async Task A_settings_file_cannot_turn_production_into_staging()
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();

        foreach (var name in new[] { "Staging", "Development" })
        {
            kit.WriteImageFile("appsettings.json", $$"""{ "environment": "{{name}}", "ASPNETCORE_ENVIRONMENT": "{{name}}", "DOTNET_ENVIRONMENT": "{{name}}", "Demo": { "AllowOutsideDevelopment": "true" } }""");
            kit.WriteImageFile("hostsettings.json", $$"""{ "environment": "{{name}}" }""");

            var run = await kit.RunAsync(Settings(kit, connection, "Production", "true", AcsTestKit.NewPassword()), DemoSeed);

            Assert.Equal(1, run.ExitCode);
            Assert.Contains("refused outside Development and Staging", run.Output);
        }

        Assert.False(await StagingDemoQaProcessKit.DatabaseExistsAsync(connection));
    }
}
