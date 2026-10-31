using System.Reflection;
using OMTS.Application.Common.Messages;

namespace OMTS.Application.UnitTests.Common.Messages;

/// <summary>
/// Every code the API sends is a code of the SRS 27/10/2026 catalogue (checked-in copy in
/// <c>srs-2710-message-codes.txt</c>) or an old code documented in <see cref="MessageCodes.Legacy.KeptAsCode"/>.
/// </summary>
public sealed class MessageCatalogueTests
{
    private static readonly Lazy<HashSet<string>> SrsCodes = new(() => File
        .ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Common", "Messages", "srs-2710-message-codes.txt"))
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith('#'))
        .ToHashSet(StringComparer.Ordinal));

    [Fact]
    public void The_checked_in_catalogue_has_the_codes_of_the_srs()
    {
        Assert.Equal(145, SrsCodes.Value.Count);
        Assert.All(SrsCodes.Value, code => Assert.Matches("^MSG-(GEN|FARM|CYCLE|WATER|HARVEST|TRACE|ORIGIN|ACCOUNT|PURCHASE|LABOR|SALE|DEBT|CLAIM)-[0-9]{2}$", code));
    }

    [Fact]
    public void Every_new_code_constant_is_in_the_srs_catalogue()
    {
        var newCodes = Constants(typeof(MessageCodes));

        Assert.NotEmpty(newCodes);
        Assert.All(newCodes, code => Assert.Contains(code, SrsCodes.Value));
    }

    [Fact]
    public void Every_user_message_has_an_srs_code_or_a_documented_legacy_code()
    {
        var messages = AllUserMessages();

        Assert.All(messages, m => Assert.True(
            SrsCodes.Value.Contains(m.Message.Code) || MessageCodes.Legacy.KeptAsCode.Contains(m.Message.Code),
            $"{m.Member}: {m.Message.Code} is neither in the SRS 27/10 catalogue nor in MessageCodes.Legacy.KeptAsCode."));
    }

    [Fact]
    public void A_legacy_code_is_an_old_code_and_only_comes_with_a_new_code()
    {
        var oldCodes = Constants(typeof(MessageCodes.Legacy)).ToHashSet(StringComparer.Ordinal);

        foreach (var (member, message) in AllUserMessages())
        {
            if (message.LegacyCode is { } legacy)
            {
                Assert.True(oldCodes.Contains(legacy), $"{member}: legacy code {legacy} is not in MessageCodes.Legacy.");
                Assert.True(SrsCodes.Value.Contains(message.Code), $"{member}: {legacy} is sent as legacy code next to {message.Code}, which is not an SRS code.");
            }
            else
            {
                Assert.True(
                    MessageCodes.Legacy.KeptAsCode.Contains(message.Code) || MessageCodes.NewWithoutLegacyCode.Contains(message.Code),
                    $"{member}: {message.Code} names no legacy code; list it in MessageCodes.NewWithoutLegacyCode if the situation is new.");
            }
        }
    }

    [Fact]
    public void A_new_message_without_legacy_code_is_an_srs_code()
    {
        Assert.All(MessageCodes.NewWithoutLegacyCode, code => Assert.Contains(code, SrsCodes.Value));
    }

    [Fact]
    public void Every_kept_legacy_code_is_an_old_code_still_used_by_a_message()
    {
        var used = AllUserMessages().Select(m => m.Message.Code).ToHashSet(StringComparer.Ordinal);

        Assert.All(MessageCodes.Legacy.KeptAsCode, code =>
        {
            Assert.DoesNotContain(code, SrsCodes.Value);
            Assert.Contains(code, used);
        });
    }

    private static IReadOnlyList<string> Constants(Type type) => type
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToList();

    /// <summary>Every public message of <see cref="UserMessages"/>: the fields, and the factories called with sample values.</summary>
    private static IReadOnlyList<(string Member, UserMessage Message)> AllUserMessages()
    {
        var type = typeof(UserMessages);
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(UserMessage))
            .Select(f => (f.Name, (UserMessage)f.GetValue(null)!));
        var factories = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(UserMessage))
            .Select(m => (m.Name, (UserMessage)m.Invoke(null, m.GetParameters().Select(SampleValue).ToArray())!));
        var all = fields.Concat(factories).ToList();
        Assert.NotEmpty(all);
        return all;
    }

    private static object? SampleValue(ParameterInfo parameter) => parameter switch
    {
        { Name: "legacyCode" } => MessageCodes.Legacy.Msg03,
        { ParameterType: var t } when t == typeof(string) => "Sample",
        { ParameterType: var t } when t == typeof(int) => 3,
        { ParameterType: var t } when t == typeof(bool) => true,
        { ParameterType: var t } when t == typeof(DateOnly) => new DateOnly(2026, 10, 1),
        { ParameterType: var t } when t == typeof(DateTimeOffset) => new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero),
        { ParameterType: var t } when t == typeof(IReadOnlyList<string>) => new[] { "Sample" },
        { ParameterType: var t } when t == typeof(IReadOnlyList<WaterValueOutside>) =>
            new[] { new WaterValueOutside(WaterValueOutside.Ph, 9.1m, null, WaterValueDirection.High, 7.5m, 8.5m) },
        _ => throw new InvalidOperationException($"No sample value for parameter {parameter.Name} ({parameter.ParameterType.Name})."),
    };
}
