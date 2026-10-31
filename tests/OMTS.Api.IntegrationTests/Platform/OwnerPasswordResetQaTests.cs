using OMTS.Application.Common.Interfaces.Messaging;
using System.Net.Http.Json;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.Commands;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>
/// QA of <c>seed --reset-owner-password</c> (OMTS-214): which account it may touch, what it lifts and ends, and
/// the real command-line entry point. The username key is always set (empty = not set), so a developer's
/// user-secrets cannot change the outcome.
/// </summary>
public class OwnerPasswordResetQaTests
{
    // Generated per run, so no password-like literal is committed (secret scanners).
    private static readonly string ResetPassword = NewPassword();

    [Fact]
    public async Task With_several_Owners_the_configured_username_chooses_one_and_the_other_Owner_is_untouched()
    {
        await using var api = new AuthApiFactory();
        var chosen = await api.AddAccountAsync($"owner-a-{NewCode()}", Password);
        var other = await api.AddAccountAsync($"owner-b-{NewCode()}", Password);
        using var chosenSession = await api.LoginAsync(chosen.Username);
        using var otherSession = await api.LoginAsync(other.Username);
        var otherHash = await HashAsync(api, other.Id);

        var outcome = await ResetAsync(api, chosen.Username);

        Assert.Equal(DeploymentBootstrapper.Success, outcome.ExitCode);
        Assert.Contains(chosen.Username, Assert.Single(outcome.Messages));
        Assert.Equal(HttpStatusCode.Unauthorized, (await chosenSession.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherSession.GetAsync("/api/me")).StatusCode);
        Assert.Equal(otherHash, await HashAsync(api, other.Id));
        Assert.True((await LoginTokensAsync(api.CreateClient(), chosen.Username, ResetPassword)).GetProperty("mustChangePassword").GetBoolean());
        var audit = await api.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "Account.PasswordReset"));
        Assert.Equal(chosen.Id, audit.RecordId);
        Assert.DoesNotContain(await HashAsync(api, chosen.Id), audit.After);
    }

    [Fact]
    public async Task With_several_Owners_and_no_username_the_reset_is_refused_and_changes_nothing()
    {
        await using var api = new AuthApiFactory();
        var first = await api.AddAccountAsync($"owner-a-{NewCode()}", Password);
        var second = await api.AddAccountAsync($"owner-b-{NewCode()}", Password);
        var hashes = (await HashAsync(api, first.Id), await HashAsync(api, second.Id));

        var outcome = await ResetAsync(api, username: "");

        Assert.Equal(DeploymentBootstrapper.Failure, outcome.ExitCode);
        Assert.Contains("Several Cooperative Owners exist", Assert.Single(outcome.Messages));
        Assert.Equal(hashes, (await HashAsync(api, first.Id), await HashAsync(api, second.Id)));
        Assert.False(await api.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "Account.PasswordReset")));
    }

    [Fact]
    public async Task The_only_Owner_is_reset_without_a_configured_username()
    {
        await using var api = new AuthApiFactory();
        var owner = await api.AddAccountAsync($"owner-{NewCode()}", Password);

        var outcome = await ResetAsync(api, username: "");

        Assert.Equal(DeploymentBootstrapper.Success, outcome.ExitCode);
        Assert.True((await LoginTokensAsync(api.CreateClient(), owner.Username, ResetPassword)).GetProperty("mustChangePassword").GetBoolean());
    }

    // The command must never become a way to set the password of a staff or trader account.
    [Theory]
    [InlineData(SubjectType.Staff)]
    [InlineData(SubjectType.Trader)]
    public async Task A_configured_username_of_a_non_Owner_account_is_refused_and_that_password_is_untouched(SubjectType subjectType)
    {
        await using var api = new AuthApiFactory();
        await api.AddAccountAsync($"owner-{NewCode()}", Password);
        var notOwner = await api.AddAccountAsync($"user-{NewCode()}", Password, subjectType);
        var hash = await HashAsync(api, notOwner.Id);

        var outcome = await ResetAsync(api, notOwner.Username);

        Assert.Equal(DeploymentBootstrapper.Failure, outcome.ExitCode);
        Assert.Equal(hash, await HashAsync(api, notOwner.Id));
        Assert.False((await api.QueryAsync(db => db.Accounts.SingleAsync(a => a.Id == notOwner.Id))).MustChangePassword);
    }

    // The Owner locked out by wrong guesses (MSG69) can log in with the new temporary password at once, and the
    // phone registered by an ended session no longer receives push.
    [Fact]
    public async Task The_reset_lifts_a_temporary_login_lock_and_stops_the_push_of_the_Owners_devices()
    {
        await using var api = new AuthApiFactory();
        var owner = await api.AddAccountAsync($"owner-{NewCode()}", Password);
        using var phone = await api.LoginAsync(owner.Username);
        Assert.Equal(HttpStatusCode.NoContent,
            (await phone.PostAsJsonAsync("/api/me/devices", new { token = "owner-phone", platform = "ios", permissionGranted = true })).StatusCode);
        for (var attempt = 0; attempt < Account.MaxFailedLogins; attempt++)
        {
            await api.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = owner.Username, password = ResetPassword + "x" });
        }

        var lockedOut = await api.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = owner.Username, password = Password });
        var outcome = await ResetAsync(api, owner.Username);
        var login = await api.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = owner.Username, password = ResetPassword });

        Assert.Equal("MSG-ACCOUNT-06", await lockedOut.ErrorCodeAsync());
        Assert.Equal(DeploymentBootstrapper.Success, outcome.ExitCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Empty(await PushDevicesAsync(api, owner.Id));
    }

    // The real entry point (Program → BootstrapCommand) with the real container: exit code 0, the console line
    // names the Owner and never contains the password.
    [Fact]
    public async Task The_command_line_entry_point_resets_the_Owner_and_prints_no_password()
    {
        await using var api = new AuthApiFactory();
        var owner = await api.AddAccountAsync($"owner-{NewCode()}", Password);
        await using var host = WithReset(api, owner.Username);
        // Synchronized: other test classes running meanwhile may write to the console too.
        var captured = new StringWriter();
        var console = TextWriter.Synchronized(captured);
        var (originalOut, originalError) = (Console.Out, Console.Error);

        int exitCode;
        Console.SetOut(console);
        Console.SetError(console);
        try
        {
            exitCode = await BootstrapCommand.RunAsync(host.Services, ["--environment", "Production", "seed", "--reset-owner-password"]);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        string printed;
        lock (console)
        {
            printed = captured.ToString();
        }

        Assert.Equal(DeploymentBootstrapper.Success, exitCode);
        Assert.Contains(owner.Username, printed);
        Assert.DoesNotContain(ResetPassword, printed);
        Assert.DoesNotContain(api.Logs.Lines, line => line.Contains(ResetPassword));
        Assert.Equal(HttpStatusCode.OK, (await api.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = owner.Username, password = ResetPassword })).StatusCode);
    }

    private static WebApplicationFactory<Program> WithReset(WebApplicationFactory<Program> api, string username) =>
        api.WithWebHostBuilder(b => b
            .UseSetting(BootstrapSettings.OwnerTemporaryPasswordKey, ResetPassword)
            .UseSetting(BootstrapSettings.OwnerUsernameKey, username));

    private static async Task<BootstrapOutcome> ResetAsync(WebApplicationFactory<Program> api, string username)
    {
        await using var host = WithReset(api, username);
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OwnerPasswordReset>().RunAsync(CancellationToken.None);
    }

    private static Task<string> HashAsync(AuthApiFactory api, Guid accountId) =>
        api.QueryAsync(db => db.Accounts.Where(a => a.Id == accountId).Select(a => a.PasswordHash).SingleAsync());

    /// <summary>The device tokens of a push sent to the account now.</summary>
    private static async Task<IReadOnlyList<string>> PushDevicesAsync(AuthApiFactory api, Guid accountId)
    {
        using (var scope = api.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<INotificationDispatcher>().NotifyAsync(
                [accountId], NotificationTypes.WaterAlert, "QA", null, null, null, isMandatory: true, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        return api.Services.GetRequiredService<FakePushSender>().Inbox.List(accountId.ToString())[0].Devices ?? [];
    }
}
