using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.Commands;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Models;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Bootstrap.Demo;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Platform.DemoOnSharedServerKit;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>The real command on a Staging host (council decision 4, OMTS-216): what it refuses, and what it leaves behind.</summary>
public class DemoOnSharedServerTests
{
    public static TheoryData<string, string, string?> RefusedRuns => new()
    {
        { "Staging", "false", AcsTestKit.NewPassword() },
        { "Staging", "true", null },
        { "Staging", "true", "   " },
        { "Staging", "true", DemoSeedGate.WellKnownPassword },
        { "Staging", "true", "a1" },
        { "Production", "true", AcsTestKit.NewPassword() },
        { "QA", "true", AcsTestKit.NewPassword() },
    };

    [Theory]
    [MemberData(nameof(RefusedRuns))]
    public async Task A_refused_run_writes_nothing_at_all(string environment, string flag, string? password)
    {
        await using var host = Host(new AuthApiFactory(), environment, flag, password);

        var refused = await RunAsync(host, demo: true);

        Assert.Equal(DeploymentBootstrapper.Failure, refused.ExitCode);
        Assert.Contains(refused.Messages, m => m.Contains("refused"));
        // The two shortest values ("a1", spaces) could be part of any sentence.
        if (password is { Length: > 3 })
        {
            Assert.DoesNotContain(refused.Messages, m => m.Contains(password));
        }

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        // The status catalog comes with EnsureCreated, so its absence shows the gate came before the database step too.
        Assert.False(await db.Statuses.AnyAsync());
        Assert.False(await db.Cooperatives.AnyAsync());
        Assert.False(await db.Accounts.AnyAsync());
        Assert.False(await db.FarmingZones.AnyAsync());
        Assert.False(await db.AuditEvents.AnyAsync());
    }

    // SQL Server: a refusal creates no database and applies no migration; the real seed then works on it.
    [SqlServerFact]
    public async Task A_refused_run_on_sql_server_does_not_touch_the_database_then_a_good_secret_gives_every_account_its_login()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-demo-files-{Guid.NewGuid():N}");
        var secret = AcsTestKit.NewPassword();
        await using var database = SqlServerTestDatabase.Create();
        try
        {
            await using (var refused = OnSqlServer(database, storageRoot, DemoSeedGate.WellKnownPassword))
            {
                Assert.Equal(DeploymentBootstrapper.Failure, (await RunAsync(refused, demo: true)).ExitCode);
            }

            await using (var db = database.CreateContext())
            {
                Assert.False(await db.Database.CanConnectAsync());
            }

            await using var api = OnSqlServer(database, storageRoot, secret);
            var outcome = await RunAsync(api, demo: true);

            Assert.True(outcome.ExitCode == DeploymentBootstrapper.Success, string.Join(Environment.NewLine, outcome.Messages));
            Assert.Contains(outcome.Messages, m => m.StartsWith("Demo "));
            await AssertEveryDemoLoginUsesAsync(api, secret);
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_good_secret_reaches_every_demo_account_and_no_message_log_or_audit_row_holds_it()
    {
        var secret = AcsTestKit.NewPassword();
        var factory = new AuthApiFactory();
        await using var api = Host(factory, "Staging", "true", secret);

        var first = await RunAsync(api, demo: true);

        Assert.True(first.ExitCode == DeploymentBootstrapper.Success, string.Join(Environment.NewLine, first.Messages));
        await AssertEveryDemoLoginUsesAsync(api, secret);
        Assert.DoesNotContain(first.Messages.Concat(factory.Logs.Lines), line => line.Contains(secret));
        using var scope = api.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().AuditEvents.ToListAsync();
        Assert.DoesNotContain(audit, e => $"{e.Before}{e.After}".Contains(secret));
    }

    // README: the forced change needs no SMS, so this branch stays usable on a server without one.
    [Fact]
    public async Task The_forced_password_change_of_the_demo_account_works_without_sms()
    {
        var secret = AcsTestKit.NewPassword();
        var newPassword = AcsTestKit.NewPassword();
        await using var api = Host(new AuthApiFactory(), "Staging", "true", secret);
        await RunAsync(api, demo: true);
        var client = api.CreateClient();
        var tokens = await AcsTestKit.LoginTokensAsync(client, "nhanvien.doimk", secret);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());

        var changed = await client.PostAsJsonAsync("/api/me/password", new { newPassword, passwordConfirmation = newPassword });

        Assert.True(changed.StatusCode == HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
        var again = await AcsTestKit.LoginTokensAsync(api.CreateClient(), "nhanvien.doimk", newPassword);
        Assert.False(again.GetProperty("mustChangePassword").GetBoolean());
    }

    // The seed never resets a password it already set, so a new secret needs a fresh database (README).
    [Fact]
    public async Task A_rerun_with_another_secret_changes_no_account()
    {
        var secret = AcsTestKit.NewPassword();
        var factory = new AuthApiFactory();
        await using var api = Host(factory, "Staging", "true", secret);
        await RunAsync(api, demo: true);
        await using var rotated = Host(factory, "Staging", "true", AcsTestKit.NewPassword());

        var rerun = await RunAsync(rotated, demo: true);

        Assert.Equal(DeploymentBootstrapper.Success, rerun.ExitCode);
        // A step the Payment Notification Service refuses (the receiving bank account without a provider) is skipped again, not "already there".
        Assert.All(rerun.Messages.Where(m => m.StartsWith("Demo ") && !m.Contains("skipped", StringComparison.OrdinalIgnoreCase)), m => Assert.EndsWith("already there.", m));
        await AssertEveryDemoLoginUsesAsync(rotated, secret);
    }

    // The flag and the secret belong to the demo: a plain bootstrap stays the handover of README "Deployment".
    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task A_bootstrap_without_demo_ignores_the_flag_and_the_secret(string environment)
    {
        var secret = AcsTestKit.NewPassword();
        await using var host = Host(new AuthApiFactory(), environment, "true", secret);

        var outcome = await RunAsync(host, demo: false);

        Assert.Equal(DeploymentBootstrapper.Success, outcome.ExitCode);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var owner = await db.Accounts.SingleAsync();
        Assert.True(owner.MustChangePassword);
        Assert.Null(owner.PhoneVerifiedAt);
        Assert.Equal(PasswordCheck.Failed, scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Verify(owner.PasswordHash, secret));
        Assert.False(await db.FarmingZones.AnyAsync());
    }

    // The real entry point: the words of the command line reach the same gate.
    [Fact]
    public async Task The_command_line_exits_with_1_in_production_with_the_flag()
    {
        await using var production = Host(new AuthApiFactory(), "Production", "true", AcsTestKit.NewPassword());

        var exitCode = await BootstrapCommand.RunAsync(production.Services, ["--environment", "Production", "seed", "--bootstrap", "--demo"]);

        Assert.Equal(DeploymentBootstrapper.Failure, exitCode);
    }
}
