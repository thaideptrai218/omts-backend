using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Models;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Bootstrap.Demo;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Platform.DemoOnSharedServerKit;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>Which databases the demo runs on (H1), what a rerun leaves alone (H2), a plain bootstrap outside Development (M1).</summary>
public partial class DemoOnSharedServerDatabaseRuleTests
{
    private static async Task<int> MarksAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().AuditEvents.CountAsync(e => e.Action == DemoSeedGate.DemoSeededAction);
    }

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.Username).ToListAsync();
        return string.Join(
            "\n",
            accounts.Select(a => $"{a.Username}|{a.PasswordHash}|{a.MustChangePassword}|{a.PhoneVerifiedAt:O}|{a.TemporaryPasswordIssuedAt:O}")
                .Append($"{await db.AuditEvents.CountAsync()},{await db.FarmingZones.CountAsync()},{await db.Employees.CountAsync()}"));
    }

    [Fact]
    public async Task H1_a_database_with_accounts_the_demo_did_not_seed_is_refused_with_nothing_written_and_no_password_named()
    {
        var secret = AcsTestKit.NewPassword();
        await using var host = Host(new AuthApiFactory(), "Staging", "true", secret);
        await RunAsync(host, demo: false);
        var before = await SnapshotAsync(host);

        var refused = await RunAsync(host, demo: true);

        Assert.Equal(DeploymentBootstrapper.Failure, refused.ExitCode);
        var message = Assert.Single(refused.Messages);
        Assert.Contains("only runs on an empty database or on its own", message);
        Assert.DoesNotContain(secret, message);
        Assert.DoesNotContain(DemoSeedGate.WellKnownPassword, message);
        Assert.DoesNotContain(BootstrapTests.OwnerTemporaryPassword, message);
        Assert.Equal(before, await SnapshotAsync(host));
    }

    [Fact]
    public async Task H1_the_run_that_seeds_an_empty_database_writes_one_mark_and_a_rerun_adds_none()
    {
        var secret = AcsTestKit.NewPassword();
        await using var host = Host(new AuthApiFactory(), "Staging", "true", secret);
        await RunAsync(host, demo: true);
        Assert.Equal(1, await MarksAsync(host));

        Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(host, demo: true)).ExitCode);

        Assert.Equal(1, await MarksAsync(host));
    }

    [Fact]
    public async Task H1_development_writes_no_mark_and_a_plain_bootstrap_outside_it_writes_none()
    {
        await using var development = Host(new AuthApiFactory(), "Development", "false", null);
        await RunAsync(development, demo: true);
        await using var plain = Host(new AuthApiFactory(), "Staging", "true", AcsTestKit.NewPassword());
        await RunAsync(plain, demo: false);

        Assert.Equal(0, await MarksAsync(development));
        Assert.Equal(0, await MarksAsync(plain));
    }

    [Fact]
    public async Task H2_a_rerun_keeps_what_a_reset_of_the_owner_did()
    {
        var secret = AcsTestKit.NewPassword();
        await using var host = Host(new AuthApiFactory(), "Staging", "true", secret);
        await RunAsync(host, demo: true);
        using (var scope = host.Services.CreateScope())
        {
            Assert.Equal(DeploymentBootstrapper.Success, (await scope.ServiceProvider.GetRequiredService<OwnerPasswordReset>().RunAsync(CancellationToken.None)).ExitCode);
        }

        var before = await SnapshotAsync(host);

        Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(host, demo: true)).ExitCode);

        Assert.Equal(before, await SnapshotAsync(host));
    }

    // Only the mark protects this Owner: the run that wrote it stopped before the Owner step, so no demo-login row exists.
    [Fact]
    public async Task H2_a_rerun_never_sets_the_login_of_an_owner_still_on_a_temporary_password()
    {
        var secret = AcsTestKit.NewPassword();
        await using var host = Host(new AuthApiFactory(), "Staging", "true", secret);
        await RunAsync(host, demo: true);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
            var owner = await db.Accounts.SingleAsync(a => a.SubjectType == SubjectType.CooperativeOwner);
            db.AuditEvents.RemoveRange(db.AuditEvents.Where(e => e.Action == DemoSeedContext.DemoLoginSetAction && e.RecordId == owner.Id));
            owner.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(BootstrapTests.OwnerTemporaryPassword);
            owner.MustChangePassword = true;
            owner.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow;
            owner.PhoneVerifiedAt = null;
            await db.SaveChangesAsync();
        }

        var before = await SnapshotAsync(host);

        Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(host, demo: true)).ExitCode);

        Assert.Equal(before, await SnapshotAsync(host));
    }

    // Development does not change: a plain bootstrap and then the demo still gives the waiting Owner the printed password.
    [Fact]
    public async Task H2_development_still_gives_a_waiting_owner_the_demo_login()
    {
        await using var host = Host(new AuthApiFactory(), "Development", "false", null);
        await RunAsync(host, demo: false);

        Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(host, demo: true)).ExitCode);

        using var scope = host.Services.CreateScope();
        var owner = await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Accounts.SingleAsync(a => a.SubjectType == SubjectType.CooperativeOwner);
        Assert.NotEqual(PasswordCheck.Failed, scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Verify(owner.PasswordHash, DemoSeedGate.WellKnownPassword));
        Assert.False(owner.MustChangePassword);
        Assert.NotNull(owner.PhoneVerifiedAt);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task M1_a_plain_bootstrap_refuses_a_database_holding_the_well_known_password_and_writes_nothing(string environment)
    {
        var factory = new AuthApiFactory();
        await using var development = Host(factory, "Development", "false", null);
        await RunAsync(development, demo: true);
        var before = await SnapshotAsync(development);
        await using var server = Host(factory, environment, "false", null);

        var refused = await RunAsync(server, demo: false);

        Assert.Equal(DeploymentBootstrapper.Failure, refused.ExitCode);
        var message = Assert.Single(refused.Messages);
        Assert.Contains("owner", message);
        Assert.DoesNotContain(DemoSeedGate.WellKnownPassword, message);
        Assert.Equal(before, await SnapshotAsync(server));
    }

    [Fact]
    public async Task M1_development_still_runs_a_plain_bootstrap_on_its_own_demo_database()
    {
        await using var development = Host(new AuthApiFactory(), "Development", "false", null);
        await RunAsync(development, demo: true);

        Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(development, demo: false)).ExitCode);
    }
}
