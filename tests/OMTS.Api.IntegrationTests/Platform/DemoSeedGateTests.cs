using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.Internal;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Validation;
using OMTS.Infrastructure.Bootstrap.Demo;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>Who may run <c>seed --bootstrap --demo</c>, and with which password (council decision 4, OMTS-216).</summary>
public class DemoSeedGateTests
{
    [Fact]
    public void Development_needs_no_flag_and_keeps_the_well_known_password_of_the_readme()
    {
        var plain = Check("Development");
        var configured = Check("Development", ("Demo:AllowOutsideDevelopment", "false"), (DemoSeedGate.PasswordKey, AcsTestKit.NewPassword()));

        Assert.Equal((DemoSeedGate.WellKnownPassword, null), plain);
        Assert.Equal((DemoSeedGate.WellKnownPassword, null), configured);
    }

    [Fact]
    public void Staging_with_the_flag_uses_the_configured_password()
    {
        var secret = AcsTestKit.NewPassword();

        Assert.Equal((secret, null), Check("Staging", Allowed, (DemoSeedGate.PasswordKey, secret)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("1")]
    [InlineData("yes")]
    public void Staging_without_a_plain_true_flag_is_refused_and_says_what_to_set(string? flag)
    {
        var secret = AcsTestKit.NewPassword();

        var (password, refusal) = Check("Staging", (DemoSeedGate.AllowOutsideDevelopmentKey, flag), (DemoSeedGate.PasswordKey, secret));

        Assert.Null(password);
        Assert.Contains(DemoSeedGate.AllowOutsideDevelopmentKey, refusal);
        Assert.DoesNotContain(secret, refusal);
    }

    // Whatever the configuration says: nothing opens another environment, and the name has to match exactly.
    [Theory]
    [InlineData("Production")]
    [InlineData("PRODUCTION")]
    [InlineData("Test")]
    [InlineData("QA")]
    [InlineData("Staging2")]
    [InlineData("Development2")]
    public void Any_other_environment_is_refused_even_with_the_flag_and_a_good_password(string environment)
    {
        var secret = AcsTestKit.NewPassword();

        var (password, refusal) = Check(environment, Allowed, (DemoSeedGate.PasswordKey, secret));

        Assert.Null(password);
        Assert.Contains("refused outside Development and Staging", refusal);
        Assert.DoesNotContain(secret, refusal);
    }

    public static TheoryData<string, string?> RefusedPasswords => new()
    {
        { "not set", null },
        { "empty", "" },
        { "whitespace", "   " },
        { "well-known", DemoSeedGate.WellKnownPassword },
        { "well-known in other letter case", DemoSeedGate.WellKnownPassword.ToUpperInvariant() },
        { "well-known with a space", " " + DemoSeedGate.WellKnownPassword },
        { "ends with a newline", AcsTestKit.NewPassword() + "\n" },
        { "too short", "a1" },
        { "no digit", new string('a', PasswordPolicy.MinimumLength + 4) },
        { "no letter", new string('1', PasswordPolicy.MinimumLength + 4) },
        { "too long", "a1" + new string('b', PasswordPolicy.MaximumLength) },
    };

    [Theory]
    [MemberData(nameof(RefusedPasswords))]
    public void A_bad_password_is_refused_in_staging_naming_the_key_never_the_value(string why, string? value)
    {
        var (password, refusal) = Check("Staging", Allowed, (DemoSeedGate.PasswordKey, value));

        Assert.Null(password);
        Assert.Contains(DemoSeedGate.PasswordKey, refusal);
        if (!string.IsNullOrWhiteSpace(value))
        {
            Assert.False(refusal!.Contains(value.Trim(), StringComparison.OrdinalIgnoreCase), why);
        }
    }

    private static readonly (string, string?) Allowed = (DemoSeedGate.AllowOutsideDevelopmentKey, "true");

    private static (string? Password, string? Refusal) Check(string environment, params (string Key, string? Value)[] settings) =>
        DemoSeedGate.Check(
            new HostingEnvironment { EnvironmentName = environment },
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
                .Build());
}
