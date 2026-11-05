using System.Text.RegularExpressions;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>A real SQL Server theory: skipped, like <see cref="SqlServerFactAttribute"/>, on a machine without one.</summary>
public sealed class StagingDemoQaSqlServerTheoryAttribute : TheoryAttribute
{
    public StagingDemoQaSqlServerTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerFactAttribute.VariableName)))
        {
            Skip = $"Set {SqlServerFactAttribute.VariableName} to a SQL Server connection string to run database tests.";
        }
    }
}

/// <summary>Reads the committed files a deployer gets, from the repository this test was built in.</summary>
internal static partial class StagingDemoQaRepositoryFiles
{
    public static string Root()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "docker-compose.yml")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>The KEY=value lines of .env.example, read rather than repeated so no secret-like literal is committed.</summary>
    public static Dictionary<string, string> EnvExampleValues() =>
        File.ReadAllLines(Path.Combine(Root(), ".env.example"))
            .Select(line => EnvLine().Match(line))
            .Where(match => match.Success)
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);

    [GeneratedRegex(@"^([A-Z_]+)=(.+)$")]
    private static partial Regex EnvLine();
}

public class StagingDemoQaRepositoryTests
{
    // Requirement: a dedicated flag and a secret of each environment. So no committed file may hold either.
    [Fact]
    public void No_committed_settings_file_compose_file_or_env_example_sets_a_demo_key()
    {
        var root = StagingDemoQaRepositoryFiles.Root();
        string[] files =
        [
            ".env.example", "docker-compose.yml", "Dockerfile",
            "src/OMTS.Api/appsettings.json", "src/OMTS.Api/appsettings.Development.json", "src/OMTS.Api/Properties/launchSettings.json",
        ];

        foreach (var file in files)
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            Assert.False(
                Regex.IsMatch(text, @"Demo__|""Demo""\s*:|Demo:(AllowOutsideDevelopment|Password)|AllowOutsideDevelopment", RegexOptions.IgnoreCase),
                file);
        }

        Assert.False(File.Exists(Path.Combine(root, "src/OMTS.Api/appsettings.Staging.json")));
        Assert.False(File.Exists(Path.Combine(root, "src/OMTS.Api/appsettings.Production.json")));
    }

    // docker-compose stays Development, so the demo keys never apply to it and it never pretends to be the shared server.
    [Fact]
    public void Docker_compose_is_fixed_to_development_and_warns()
    {
        var compose = File.ReadAllText(Path.Combine(StagingDemoQaRepositoryFiles.Root(), "docker-compose.yml"));

        Assert.Contains("ASPNETCORE_ENVIRONMENT: Development", compose);
        Assert.DoesNotContain("ASPNETCORE_ENVIRONMENT: ${", compose);
        Assert.Contains("Never the shared server", compose);
    }
}
