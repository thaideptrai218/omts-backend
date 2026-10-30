using System.Net.Http.Json;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.Commands;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary><c>seed --reset-owner-password</c> (OMTS-214): recovery of an Owner who missed the 24-hour window.</summary>
public class OwnerPasswordResetTests
{
    private static readonly string BootstrapPassword = BootstrapTests.OwnerTemporaryPassword;

    // Generated per run, so no password-like literal is committed (secret scanners).
    private static readonly string ResetPassword = AcsTestKit.NewPassword();

    [Fact]
    public async Task Owner_who_missed_the_window_gets_a_new_temporary_password_and_every_session_ends()
    {
        var host = new AuthApiFactory();
        await using var api = BootstrapTests.WithSecrets(host);
        await BootstrapAsync(api);
        var client = api.CreateClient();
        var firstLogin = await AcsTestKit.LoginTokensAsync(client, "owner", BootstrapPassword);
        await AgeTemporaryPasswordAsync(api, TimeSpan.FromHours(25));
        var expired = await client.PostAsJsonAsync("/api/auth/login", new { identifier = "owner", password = BootstrapPassword });

        await using var resetHost = WithResetPassword(api);
        var outcome = await ResetAsync(resetHost);

        var oldRefresh = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = firstLogin.GetProperty("refreshToken").GetString() });
        var newLogin = await AcsTestKit.LoginTokensAsync(client, "owner", ResetPassword);
        Assert.NotEqual(HttpStatusCode.OK, expired.StatusCode);
        Assert.Equal(DeploymentBootstrapper.Success, outcome.ExitCode);
        Assert.Equal(HttpStatusCode.Unauthorized, oldRefresh.StatusCode);
        Assert.True(newLogin.GetProperty("mustChangePassword").GetBoolean());

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var owner = await db.Accounts.SingleAsync(a => a.Username == "owner");
        var audit = await db.AuditEvents.SingleAsync(e => e.Action == "Account.PasswordReset");
        Assert.Equal(owner.Id, audit.RecordId);
        Assert.Null(audit.ActorAccountId);
        Assert.Contains("seed --reset-owner-password", audit.After);
        Assert.DoesNotContain(ResetPassword, audit.After);
        Assert.DoesNotContain(outcome.Messages.Concat(host.Logs.Lines), line => line.Contains(ResetPassword));
    }

    [Fact]
    public async Task Without_an_Owner_the_reset_is_refused()
    {
        await using var api = WithResetPassword(new AuthApiFactory());

        var outcome = await ResetAsync(api);

        Assert.Equal(DeploymentBootstrapper.Failure, outcome.ExitCode);
        Assert.Contains("No Cooperative Owner exists", Assert.Single(outcome.Messages));
    }

    [Theory]
    [InlineData(null, "Missing bootstrap secrets: Bootstrap:Owner:TemporaryPassword.")]
    [InlineData("short1", "Bootstrap:Owner:TemporaryPassword must have at least 8 characters with a letter and a digit.")]
    public async Task Missing_or_weak_password_is_refused_and_changes_nothing(string? password, string expected)
    {
        await using var api = BootstrapTests.WithSecrets(new AuthApiFactory());
        await BootstrapAsync(api);
        var before = await OwnerHashAsync(api);
        await using var resetHost = api.WithWebHostBuilder(b => b.UseSetting(BootstrapSettings.OwnerTemporaryPasswordKey, password ?? string.Empty));

        var outcome = await ResetAsync(resetHost);

        Assert.Equal(DeploymentBootstrapper.Failure, outcome.ExitCode);
        Assert.Equal(expected, Assert.Single(outcome.Messages));
        Assert.Equal(before, await OwnerHashAsync(api));
    }

    // The bootstrap's key left in place would hand out the expired temporary password again.
    [Fact]
    public async Task The_Owners_current_password_is_refused_as_the_new_one()
    {
        await using var api = BootstrapTests.WithSecrets(new AuthApiFactory());
        await BootstrapAsync(api);
        var before = await OwnerHashAsync(api);

        var outcome = await ResetAsync(api);

        Assert.Equal(DeploymentBootstrapper.Failure, outcome.ExitCode);
        Assert.Equal("Bootstrap:Owner:TemporaryPassword is the Owner's current password; set a new value first.", Assert.Single(outcome.Messages));
        Assert.Equal(before, await OwnerHashAsync(api));
    }

    [Fact]
    public async Task Configured_username_that_is_not_an_Owner_is_refused()
    {
        await using var api = BootstrapTests.WithSecrets(new AuthApiFactory());
        await BootstrapAsync(api);
        await using var resetHost = WithResetPassword(api).WithWebHostBuilder(b => b.UseSetting(BootstrapSettings.OwnerUsernameKey, "someone-else"));

        var outcome = await ResetAsync(resetHost);

        Assert.Equal(DeploymentBootstrapper.Failure, outcome.ExitCode);
        Assert.Contains(BootstrapSettings.OwnerUsernameKey, Assert.Single(outcome.Messages));
    }

    [Fact]
    public void Reset_command_is_found_and_its_flag_is_not_passed_to_the_host()
    {
        string[] args = ["--environment", "Production", "seed", "--reset-owner-password"];

        Assert.True(BootstrapCommand.IsInvoked(args));
        Assert.Equal(["--environment", "Production"], BootstrapCommand.HostArguments(args));
        Assert.True(BootstrapCommand.IsInvoked(["--reset-owner-password"]));
    }

    // Exactly one action: the reset alone, never with --bootstrap or --demo, and never without "seed".
    [Theory]
    [InlineData("--reset-owner-password")]
    [InlineData("seed --bootstrap --reset-owner-password")]
    [InlineData("seed --reset-owner-password --demo")]
    public async Task Ambiguous_reset_command_fails_with_the_usage_text(string commandLine)
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        Assert.Equal(DeploymentBootstrapper.Failure, await BootstrapCommand.RunAsync(services, commandLine.Split(' ')));
    }

    private static WebApplicationFactory<Program> WithResetPassword(WebApplicationFactory<Program> api) =>
        api.WithWebHostBuilder(b => b.UseSetting(BootstrapSettings.OwnerTemporaryPasswordKey, ResetPassword));

    private static async Task BootstrapAsync(WebApplicationFactory<Program> api)
    {
        using var scope = api.Services.CreateScope();
        Assert.Equal(DeploymentBootstrapper.Success, (await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(false, CancellationToken.None)).ExitCode);
    }

    private static async Task<BootstrapOutcome> ResetAsync(WebApplicationFactory<Program> api)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OwnerPasswordReset>().RunAsync(CancellationToken.None);
    }

    private static async Task AgeTemporaryPasswordAsync(WebApplicationFactory<Program> api, TimeSpan age)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var owner = await db.Accounts.SingleAsync(a => a.Username == "owner");
        owner.TemporaryPasswordIssuedAt -= age;
        await db.SaveChangesAsync();
    }

    private static async Task<string> OwnerHashAsync(WebApplicationFactory<Program> api)
    {
        using var scope = api.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Accounts.SingleAsync(a => a.SubjectType == SubjectType.CooperativeOwner)).PasswordHash;
    }
}
