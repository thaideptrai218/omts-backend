using System.Text.RegularExpressions;

namespace OMTS.Application.UnitTests.Common.Messages;

/// <summary>
/// QA of OMTS-216 PR 0.2: <see cref="MessageCatalogueTests"/> checks the messages of <c>UserMessages</c> by reflection,
/// so a code written anywhere else (a literal "MSG01" in a filter, a <c>new UserMessage(...)</c> in a service, a
/// <c>with { Code = ... }</c>) would escape it. These checks read the source of <c>src/</c> and keep every code in
/// <c>MessageCodes</c> and every message in <c>UserMessages</c>.
/// </summary>
public sealed partial class MessageCodeSourceScanQaTests
{
    private static readonly Lazy<IReadOnlyList<(string Path, string Text)>> Sources = new(() =>
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        return Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Select(path => (Path.GetRelativePath(src, path), File.ReadAllText(path)))
            .ToList();
    });

    [Fact]
    public void The_scan_sees_the_message_sources()
    {
        Assert.Contains(Sources.Value, s => s.Path.EndsWith("MessageCodes.cs", StringComparison.Ordinal));
        Assert.Contains(Sources.Value, s => s.Path.EndsWith("UserMessages.cs", StringComparison.Ordinal));
        Assert.True(Sources.Value.Count > 100, $"Only {Sources.Value.Count} source files found.");
    }

    [Fact]
    public void Message_code_literals_appear_only_in_MessageCodes()
    {
        var offenders = Sources.Value
            .Where(s => !s.Path.EndsWith($"Messages{Path.DirectorySeparatorChar}MessageCodes.cs", StringComparison.Ordinal))
            .SelectMany(s => CodeLiteral().Matches(s.Text).Select(m => $"{s.Path}: {m.Value}"))
            .ToList();

        Assert.True(offenders.Count == 0, "Message codes written outside MessageCodes:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Messages_are_built_only_in_UserMessages()
    {
        var offenders = Sources.Value
            .Where(s => !s.Path.EndsWith($"Messages{Path.DirectorySeparatorChar}UserMessages.cs", StringComparison.Ordinal))
            .SelectMany(s => MessageConstruction().Matches(s.Text).Select(m => $"{s.Path}: {m.Value}"))
            .ToList();

        Assert.True(offenders.Count == 0, "UserMessage built or re-coded outside UserMessages:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Call_sites_set_legacy_codes_only_from_MessageCodes_Legacy()
    {
        var assignments = Sources.Value
            .SelectMany(s => LegacyAssignment().Matches(s.Text).Select(m => (s.Path, Value: m.Groups["value"].Value.Trim())))
            .ToList();

        // A constant of MessageCodes.Legacy, or a parameter passed through (whose callers pass such a constant).
        Assert.NotEmpty(assignments);
        Assert.All(assignments, a => Assert.True(
            a.Value.StartsWith("MessageCodes.Legacy.", StringComparison.Ordinal) || a.Value.StartsWith("Legacy.", StringComparison.Ordinal)
                || ParameterName().IsMatch(a.Value),
            $"{a.Path}: LegacyCode = {a.Value}"));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OMTS.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"OMTS.sln not found above {AppContext.BaseDirectory}.");
    }

    // "MSG01", "MSG-GEN-01" and the technical codes of MessageCodes.Legacy, as string literals.
    [GeneratedRegex("\"(MSG\\d{2}|MSG-[A-Z]+-\\d{2}|NOT_FOUND|TOO_MANY_REQUESTS|UNEXPECTED_ERROR|PASSWORD_CHANGE_REQUIRED|NO_VERIFIED_EMAIL|LICENSE_REPLACED)\"")]
    private static partial Regex CodeLiteral();

    [GeneratedRegex(@"new\s+UserMessage\s*\(|with\s*\{[^}]*\bCode\s*=")]
    private static partial Regex MessageConstruction();

    // Only the "with { LegacyCode = … }" of call sites, not the record's own default value.
    [GeneratedRegex(@"with\s*\{\s*LegacyCode\s*=\s*(?<value>[^,}\r\n]+)")]
    private static partial Regex LegacyAssignment();

    [GeneratedRegex(@"^[a-z][A-Za-z0-9]*$")]
    private static partial Regex ParameterName();
}
