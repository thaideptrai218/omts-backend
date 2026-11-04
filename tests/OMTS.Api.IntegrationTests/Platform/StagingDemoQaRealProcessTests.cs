using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Bootstrap.Demo;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>
/// QA of OMTS-216 on the real process: which environment names and which configuration sources open the demo. Each
/// refused run must not even create the database. Tests named after a finding (L1_...) fail on purpose.
/// </summary>
public partial class StagingDemoQaRealProcessTests
{
    private const string FlagVariable = "Demo__AllowOutsideDevelopment";
    private const string PasswordVariable = "Demo__Password";
    private const string EnvironmentVariable = "ASPNETCORE_ENVIRONMENT";
    private static readonly string[] DemoSeed = ["seed", "--bootstrap", "--demo"];

    // Passes the flag check and fails the password rule: which refusal comes back tells what was read, without a database.
    private static readonly string TooShort = AcsTestKit.NewPassword()[..4];

    private static Dictionary<string, string?> Settings(StagingDemoQaProcessKit kit, string connection, string? environment, string? flag, string? password)
    {
        var settings = kit.ServerSettings(connection);
        settings[EnvironmentVariable] = environment;
        settings[FlagVariable] = flag;
        settings[PasswordVariable] = password;
        return settings;
    }

    [SqlServerFact]
    public async Task No_other_environment_name_opens_the_demo_with_or_without_the_flag_and_nothing_is_created()
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        var secret = AcsTestKit.NewPassword();
        var wrong = new List<string>();
        string?[] names = ["Production", "PRODUCTION", "production", " Production", null, "", "QA", "Test", "Staging2", " Staging", "Staging ", "Stag ing", " Development", "Development2"];

        foreach (var name in names)
        {
            foreach (var flag in new[] { "true", null })
            {
                var run = await kit.RunAsync(Settings(kit, connection, name, flag, secret), DemoSeed);
                if (run.ExitCode != 1 || !run.Output.Contains("refused outside Development and Staging") || run.Output.Contains(secret))
                {
                    wrong.Add($"[{name}] flag {flag}: exit {run.ExitCode}: {run.Output.Replace(secret, "<secret>")}");
                }
            }
        }

        Assert.Empty(wrong);
        Assert.False(await StagingDemoQaProcessKit.DatabaseExistsAsync(connection));
    }

    // The name is compared without regard to case, like every IsStaging in .NET: other spellings get the same gate.
    [SqlServerFact]
    public async Task Staging_in_another_letter_case_is_the_same_gate()
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        foreach (var name in new[] { "staging", "STAGING" })
        {
            var noFlag = await kit.RunAsync(Settings(kit, connection, name, null, AcsTestKit.NewPassword()), DemoSeed);
            var withFlag = await kit.RunAsync(Settings(kit, connection, name, "true", TooShort), DemoSeed);

            Assert.Equal(1, noFlag.ExitCode);
            Assert.Contains("unless Demo:AllowOutsideDevelopment is true", noFlag.Output);
            Assert.Equal(1, withFlag.ExitCode);
            Assert.Contains("Demo:Password must have", withFlag.Output);
        }

        Assert.False(await StagingDemoQaProcessKit.DatabaseExistsAsync(connection));
    }

    [SqlServerFact]
    public async Task The_flag_value_on_a_real_process()
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        var wrong = new List<string>();

        foreach (var closed in new[] { "1", "yes", "on", "", "   ", "false", "False", "0", "truex", "t", "enabled" })
        {
            var run = await kit.RunAsync(Settings(kit, connection, "Staging", closed, AcsTestKit.NewPassword()), DemoSeed);
            if (run.ExitCode != 1 || !run.Output.Contains("unless Demo:AllowOutsideDevelopment is true"))
            {
                wrong.Add($"[{closed}] should stay closed: exit {run.ExitCode}: {run.Output}");
            }
        }

        // bool.TryParse: any letter case and surrounding spaces. Still only the word true.
        foreach (var open in new[] { "true", "True", "TRUE", " true " })
        {
            var run = await kit.RunAsync(Settings(kit, connection, "Staging", open, TooShort), DemoSeed);
            if (run.ExitCode != 1 || !run.Output.Contains("Demo:Password must have"))
            {
                wrong.Add($"[{open}] should reach the password check: exit {run.ExitCode}: {run.Output}");
            }
        }

        // A section instead of a value has no value.
        var section = Settings(kit, connection, "Staging", null, AcsTestKit.NewPassword());
        section[FlagVariable + "__0"] = "true";
        var sectionRun = await kit.RunAsync(section, DemoSeed);
        if (!sectionRun.Output.Contains("unless Demo:AllowOutsideDevelopment is true"))
        {
            wrong.Add($"section: {sectionRun.Output}");
        }

        Assert.Empty(wrong);
        Assert.False(await StagingDemoQaProcessKit.DatabaseExistsAsync(connection));
    }

    [SqlServerFact]
    public async Task The_password_value_on_a_real_process_is_refused_naming_the_key_and_nothing_is_created()
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        var known = DemoSeedGate.WellKnownPassword;
        var wrong = new List<string>();
        string?[] refused =
        [
            null, "", "   ", "\t", known, known.ToUpperInvariant(), known.ToLowerInvariant(), " " + known, known + " ", known + "\n", (char)0x00A0 + known, known + (char)0x3000,
            TooShort, new string('a', 12), new string('1', 12), AcsTestKit.NewPassword() + new string('b', 116),
        ];

        foreach (var value in refused)
        {
            var run = await kit.RunAsync(Settings(kit, connection, "Staging", "true", value), DemoSeed);
            var leaked = !string.IsNullOrWhiteSpace(value) && run.Output.Contains(value.Trim(), StringComparison.OrdinalIgnoreCase);
            if (run.ExitCode != 1 || !run.Output.Contains("Demo:Password") || leaked)
            {
                wrong.Add($"exit {run.ExitCode}, leaked {leaked}: {run.Output}");
            }
        }

        var section = Settings(kit, connection, "Staging", "true", null);
        section[PasswordVariable + "__0"] = AcsTestKit.NewPassword();
        var sectionRun = await kit.RunAsync(section, DemoSeed);
        if (!sectionRun.Output.Contains("Demo:Password is not set"))
        {
            wrong.Add($"section: {sectionRun.Output}");
        }

        Assert.Empty(wrong);
        Assert.False(await StagingDemoQaProcessKit.DatabaseExistsAsync(connection));
    }
}
