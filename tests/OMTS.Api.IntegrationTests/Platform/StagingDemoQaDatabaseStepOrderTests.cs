using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.Commands;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Models;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Bootstrap.Demo;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Platform.DemoOnSharedServerKit;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>QA of OMTS-216: order of the steps, reruns and a crash in the middle. Named tests fail on purpose.</summary>
public partial class StagingDemoQaDatabaseTests
{
    // M1. Disclosed in README and the change list as a trap, so a PM call: only `--demo` looks. A Development copy is
    // live with the password printed in the README as soon as the API starts, in Staging and in Production.
    [StagingDemoQaSqlServerTheory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task M1_a_database_holding_the_well_known_password_does_not_go_live_outside_development(string environment)
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        try
        {
            var settings = kit.ServerSettings(connection);
            settings["ASPNETCORE_ENVIRONMENT"] = "Development";
            Assert.Equal(0, (await kit.RunAsync(settings, "seed", "--bootstrap", "--demo")).ExitCode);

            settings["ASPNETCORE_ENVIRONMENT"] = environment;
            var plain = await kit.RunAsync(settings, "seed", "--bootstrap");
            using var api = await kit.StartApiAsync(settings);
            var login = await api.Client.PostAsJsonAsync("/api/auth/login", new { identifier = "owner", password = DemoSeedGate.WellKnownPassword });

            Assert.True(
                login.StatusCode != HttpStatusCode.OK,
                $"{environment}: plain seed --bootstrap exit {plain.ExitCode}; the API started; owner logs in with the well-known password: {(int)login.StatusCode}");
        }
        finally
        {
            await StagingDemoQaProcessKit.DropDatabaseAsync(connection);
        }
    }

    // L6. A crash, then a rerun with a rotated secret: exit 0, "already there", and the first secret stays on the accounts
    // seeded before the crash. README: "The nine logins ... all use it".
    [Fact]
    public async Task L6_a_rerun_after_a_crash_with_another_secret_does_not_leave_two_passwords_behind_exit_0()
    {
        var first = AcsTestKit.NewPassword();
        var second = AcsTestKit.NewPassword();
        var factory = new AuthApiFactory();
        await using var firstRun = Host(factory, "Staging", "true", first);
        await RunAsync(firstRun, demo: false);
        var blocker = await AddAccountNamedAsync(firstRun, "ketoan");
        var crash = await Record.ExceptionAsync(() => RunAsync(firstRun, demo: true));
        Assert.NotNull(crash);
        Assert.DoesNotContain(first, crash.ToString());
        await ChangeAsync(firstRun, async (db, _) => db.Accounts.Remove(await db.Accounts.SingleAsync(a => a.Id == blocker)));

        await using var secondRun = Host(factory, "Staging", "true", second);
        var outcome = await RunAsync(secondRun, demo: true);

        using var scope = secondRun.Services.CreateScope();
        var accounts = await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Accounts.AsNoTracking().ToListAsync();
        var holdingFirst = accounts.Where(a => Holds(secondRun, a, first)).Select(a => a.Username).Order().ToList();
        Assert.True(
            outcome.ExitCode != DeploymentBootstrapper.Success || holdingFirst.Count == 0,
            $"exit {outcome.ExitCode}; still on the first secret: {string.Join(", ", holdingFirst)}");
    }

    // After a crash, with the same secret: nothing is left half done and no account stays on a random temporary password.
    [Fact]
    public async Task A_rerun_after_a_crash_with_the_same_secret_finishes_and_every_account_uses_it()
    {
        var secret = AcsTestKit.NewPassword();
        var factory = new AuthApiFactory();
        await using var host = Host(factory, "Staging", "true", secret);
        await RunAsync(host, demo: false);
        var blocker = await AddAccountNamedAsync(host, "nhanvien.chuaxacthuc");
        Assert.NotNull(await Record.ExceptionAsync(() => RunAsync(host, demo: true)));
        await ChangeAsync(host, async (db, _) => db.Accounts.Remove(await db.Accounts.SingleAsync(a => a.Id == blocker)));

        Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(host, demo: true)).ExitCode);

        await AssertEveryDemoLoginUsesAsync(host, secret);
    }

    // Order of steps: a half-seeded Staging database finished in Development is caught at the next Staging run.
    [Fact]
    public async Task A_crash_in_staging_then_a_development_run_then_staging_is_refused()
    {
        var secret = AcsTestKit.NewPassword();
        var factory = new AuthApiFactory();
        await using var staging = Host(factory, "Staging", "true", secret);
        await RunAsync(staging, demo: false);
        var blocker = await AddAccountNamedAsync(staging, "ketoan");
        Assert.NotNull(await Record.ExceptionAsync(() => RunAsync(staging, demo: true)));
        await ChangeAsync(staging, async (db, _) => db.Accounts.Remove(await db.Accounts.SingleAsync(a => a.Id == blocker)));
        await using var development = Host(factory, "Development", "false", null);
        Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(development, demo: true)).ExitCode);

        var refused = await RunAsync(staging, demo: true);

        Assert.Equal(DeploymentBootstrapper.Failure, refused.ExitCode);
        Assert.Contains("ketoan", Assert.Single(refused.Messages));
    }

    [Fact]
    public async Task An_owner_who_changed_the_password_is_left_alone_and_their_forced_steps_stay_done()
    {
        var secret = AcsTestKit.NewPassword();
        var own = AcsTestKit.NewPassword();
        await using var host = Host(new AuthApiFactory(), "Staging", "true", secret);
        await RunAsync(host, demo: false);
        await OwnerFinishesFirstLoginAsync(host, own);
        var before = await OwnerAsync(host);

        Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(host, demo: true)).ExitCode);

        var after = await OwnerAsync(host);
        Assert.True(Holds(host, after, own));
        Assert.False(Holds(host, after, secret));
        Assert.Equal(before.PhoneVerifiedAt, after.PhoneVerifiedAt);
    }

    // A rerun touches no later change: own new password, or an Owner reset that again forces a change.
    [Fact]
    public async Task A_rerun_keeps_what_testers_changed_on_the_server()
    {
        var secret = AcsTestKit.NewPassword();
        var changed = AcsTestKit.NewPassword();
        var reset = AcsTestKit.NewPassword();
        await using var host = Host(new AuthApiFactory(), "Staging", "true", secret);
        await RunAsync(host, demo: true);
        await ChangeAsync(host, async (db, hasher) =>
        {
            (await db.Accounts.SingleAsync(a => a.Username == "quanlyvung1")).PasswordHash = hasher.Hash(changed);
            var other = await db.Accounts.SingleAsync(a => a.Username == "ketoan");
            other.PasswordHash = hasher.Hash(reset);
            other.MustChangePassword = true;
            other.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow;
        });

        Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(host, demo: true)).ExitCode);

        using var scope = host.Services.CreateScope();
        var accounts = await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Accounts.AsNoTracking().ToListAsync();
        Assert.True(Holds(host, accounts.Single(a => a.Username == "quanlyvung1"), changed));
        var resetAccount = accounts.Single(a => a.Username == "ketoan");
        Assert.True(Holds(host, resetAccount, reset));
        Assert.True(resetAccount.MustChangePassword);
    }

    // PM call, not a defect: the change list and README state these on purpose. Recorded so a change is noticed.
    [Fact]
    public async Task Demo_accounts_skip_the_forced_steps_and_doimk_never_expires_on_a_shared_server_as_documented()
    {
        await using var host = Host(new AuthApiFactory(), "Staging", "true", AcsTestKit.NewPassword());
        await RunAsync(host, demo: true);

        using var scope = host.Services.CreateScope();
        var accounts = await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Accounts.AsNoTracking().ToListAsync();
        Assert.All(accounts.Where(a => a.Username != "nhanvien.chuaxacthuc"), a => Assert.NotNull(a.PhoneVerifiedAt));
        Assert.Equal(1, accounts.Count(a => a.MustChangePassword));
        var doimk = accounts.Single(a => a.Username == "nhanvien.doimk");
        Assert.True(doimk.MustChangePassword);
        Assert.Equal(2999, doimk.TemporaryPasswordIssuedAt!.Value.Year);
    }

    // The only credentials the seed writes: no session, verification code or device token of any account.
    [Fact]
    public async Task The_seed_leaves_no_session_code_or_token_that_could_be_used_by_someone_else()
    {
        await using var host = Host(new AuthApiFactory(), "Staging", "true", AcsTestKit.NewPassword());
        await RunAsync(host, demo: true);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        Assert.False(await db.LoginSessions.AnyAsync());
        Assert.False(await db.VerificationCodes.AnyAsync());
        Assert.False(await db.PushDevices.AnyAsync());
    }
}
