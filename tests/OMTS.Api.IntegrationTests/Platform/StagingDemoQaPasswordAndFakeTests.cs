using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using OMTS.Api.Controllers;
using OMTS.Api.Filters;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Bootstrap.Demo;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Platform.DemoOnSharedServerKit;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>
/// QA of OMTS-216: the values Demo:Password accepts, the well-known password chosen some other way, and the fake inbox
/// on a server. Tests named after a finding (L3_..., L4_...) fail on purpose, and stay skipped until the PM decides.
/// </summary>
public partial class StagingDemoQaPasswordAndFakeTests
{
    private static string Known => DemoSeedGate.WellKnownPassword;

    private static string? Accepted(string? value) =>
        DemoSeedGate.Check(
            new HostingEnvironment { EnvironmentName = "Staging" },
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DemoSeedGate.AllowOutsideDevelopmentKey] = "true",
                [DemoSeedGate.PasswordKey] = value,
            }).Build()).Password;

    private static string FullWidth(string text) => new(text.Select(c => (char)(c + 0xFEE0)).ToArray());

    public static TheoryData<string, string> VariantsOfTheWellKnownPassword => new()
    {
        // The same text to the eye, or after Unicode normalization.
        { "full width forms (NFKC gives the value)", FullWidth(Known) },
        { "zero-width space at the end", Known + (char)0x200B },
        { "zero-width space at the start", (char)0x200B + Known },
        { "soft hyphen inside", Known.Insert(4, ((char)0x00AD).ToString()) },
        { "Cyrillic e", Known.Replace('e', (char)0x0435) },
        { "Cyrillic o", Known.Replace('o', (char)0x043E) },
        // One keystroke away.
        { "without the at sign", Known.Replace("@", string.Empty) },
        { "one more digit", Known + "5" },
        { "exclamation mark", Known + "!" },
        { "zero for o", Known.Replace('o', '0') },
    };

    // L3. Requirement: refuse "the well-known value". The check is an exact, case-insensitive match, so a value a person
    // reads as the printed password passes it. PM call on how far "well-known" goes. Look-alikes do not make the printed
    // password itself work (test below); the risk is a guessable secret on a shared server.
    [Theory(Skip = "Waiting for the PM: does 'well-known' reach values a person reads as the printed password, look-alikes and values one keystroke away?")]
    [MemberData(nameof(VariantsOfTheWellKnownPassword))]
    public void L3_a_variant_of_the_well_known_password_is_refused(string why, string value)
    {
        Assert.True(Accepted(value) is null, $"{why}: accepted as Demo:Password");
    }

    // L3. The other passwords committed in the repository pass as well, as does anything a guess would try first.
    [Theory(Skip = "Waiting for the PM: should Demo:Password also refuse the other passwords committed in .env.example?")]
    [InlineData("OWNER_TEMPORARY_PASSWORD")]
    [InlineData("MSSQL_SA_PASSWORD")]
    public void L3_a_password_committed_in_env_example_is_refused_as_the_demo_password(string key)
    {
        Assert.True(Accepted(StagingDemoQaRepositoryFiles.EnvExampleValues()[key]) is null, $"the committed {key} is accepted as Demo:Password");
    }

    [Fact(Skip = "Waiting for the PM: should the shared secret have a stronger rule than the password rule of a personal account?")]
    public void L3_a_secret_as_weak_as_seven_equal_letters_and_a_digit_is_refused_on_a_shared_server()
    {
        Assert.True(Accepted(new string('a', 7) + "1") is null, "accepted: the password rule of a personal account is the only check on the shared secret");
    }

    // The look-alike stays a different password: the printed value does not log in, and the secret itself does.
    [Fact]
    public async Task A_look_alike_secret_does_not_make_the_printed_password_work()
    {
        var secret = FullWidth(Known);
        await using var host = Host(new AuthApiFactory(), "Staging", "true", secret);
        await RunAsync(host, demo: true);
        var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { identifier = "owner", password = Known })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { identifier = "owner", password = secret })).StatusCode);
    }

    public static TheoryData<string, string> UnusualGoodSecrets => new()
    {
        { "longest allowed", AcsTestKit.NewPassword() + new string('b', 115) },
        { "shortest allowed", AcsTestKit.NewPassword()[..7] + "7" },
        { "letters outside ASCII", AcsTestKit.NewPassword() + (char)0x1EAD + (char)0x1EA9 },
        { "json and config characters", AcsTestKit.NewPassword() + ":\"\\{}=;$%&'" },
    };

    // A good secret must also log in: a value the gate accepts and the login cannot use leaves nobody able to enter.
    [Theory]
    [MemberData(nameof(UnusualGoodSecrets))]
    public async Task Every_secret_the_gate_accepts_logs_in_and_is_in_no_message_or_log(string why, string secret)
    {
        var factory = new AuthApiFactory();
        await using var host = Host(factory, "Staging", "true", secret);

        var outcome = await RunAsync(host, demo: true);

        Assert.True(outcome.ExitCode == DeploymentBootstrapper.Success, why);
        await AssertEveryDemoLoginUsesAsync(host, secret);
        Assert.DoesNotContain(outcome.Messages.Concat(factory.Logs.Lines), line => line.Contains(secret));
    }

    // L4. The password rule does not forbid the printed value. Disclosed in the change list; not in the README, which
    // the list says holds all the traps. PM call. The next demo seed then refuses the whole server.
    [Fact(Skip = "Waiting for the PM: should a tester be stopped from choosing the well-known password on the shared server?")]
    public async Task L4_a_tester_cannot_choose_the_well_known_password_on_the_shared_server()
    {
        var secret = AcsTestKit.NewPassword();
        await using var host = Host(new AuthApiFactory(), "Staging", "true", secret);
        await RunAsync(host, demo: true);
        var client = host.CreateClient();
        var tokens = await AcsTestKit.LoginTokensAsync(client, "nhanvien.doimk", secret);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());

        var changed = await client.PostAsJsonAsync("/api/me/password", new { newPassword = Known, passwordConfirmation = Known });

        Assert.True(changed.StatusCode != HttpStatusCode.OK, $"POST /api/me/password with the printed password: {(int)changed.StatusCode}; the printed password no longer answers 401 there, and the next seed exits 1: {string.Join(' ', (await RunAsync(host, demo: true)).Messages)}");
    }

    // L4. Outside --demo the first password of the Owner is not checked at all. Same as before this branch.
    [Theory(Skip = "Waiting for the PM: should a plain bootstrap refuse the well-known password as the Owner's first password?")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task L4_a_plain_bootstrap_does_not_give_the_owner_the_well_known_password(string environment)
    {
        await using var host = Host(new AuthApiFactory(), environment, "false", null)
            .WithWebHostBuilder(b => b.UseSetting(BootstrapSettings.OwnerTemporaryPasswordKey, Known));

        var outcome = await RunAsync(host, demo: false);

        Assert.True(outcome.ExitCode != DeploymentBootstrapper.Success, $"{environment}: exit 0, the Owner's first password is the one printed in the README");
    }

    [Theory(Skip = "Waiting for the PM: should seed --reset-owner-password refuse the well-known password?")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task L4_a_reset_of_the_owner_does_not_give_the_owner_the_well_known_password(string environment)
    {
        await using var host = Host(new AuthApiFactory(), environment, "false", null);
        await RunAsync(host, demo: false);
        await using var reset = host.WithWebHostBuilder(b => b.UseSetting(BootstrapSettings.OwnerTemporaryPasswordKey, Known));
        using var scope = reset.Services.CreateScope();

        var outcome = await scope.ServiceProvider.GetRequiredService<OwnerPasswordReset>().RunAsync(CancellationToken.None);

        Assert.True(outcome.ExitCode != DeploymentBootstrapper.Success, $"{environment}: exit 0, the Owner's password is now the one printed in the README");
    }
}
