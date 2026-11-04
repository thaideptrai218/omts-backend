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

/// <summary>
/// QA of OMTS-216: what the demo step does to a database that is not empty, in what order the steps can be run, and a
/// crash in the middle. Tests named after a finding (H1_...) fail on purpose until the finding is decided.
/// </summary>
public partial class StagingDemoQaDatabaseTests
{
    private static async Task<Account> OwnerAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Accounts.AsNoTracking()
            .OrderBy(a => a.CreatedAt).FirstAsync(a => a.SubjectType == SubjectType.CooperativeOwner);
    }

    private static async Task ChangeAsync(WebApplicationFactory<Program> host, Func<OmtsDbContext, IPasswordHasher, Task> change)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        await change(db, scope.ServiceProvider.GetRequiredService<IPasswordHasher>());
        await db.SaveChangesAsync();
    }

    private static bool Holds(WebApplicationFactory<Program> host, Account account, string password)
    {
        using var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Verify(account.PasswordHash, password) != PasswordCheck.Failed;
    }

    /// <summary>A real account holding a username of the demo plans, so the employee step of the demo stops on it.</summary>
    private static async Task<Guid> AddAccountNamedAsync(WebApplicationFactory<Program> host, string username)
    {
        var id = Guid.Empty;
        await ChangeAsync(host, (db, hasher) =>
        {
            var account = new Account
            {
                Username = username,
                DisplayName = username,
                PasswordHash = hasher.Hash(AcsTestKit.NewPassword()),
                SubjectType = SubjectType.Trader,
                PhoneNumber = AcsTestKit.NewPhone(),
                StatusId = OMTS.Domain.Constants.StatusIds.Account.Active,
            };
            db.Accounts.Add(account);
            id = account.Id;
            return Task.CompletedTask;
        });
        return id;
    }
    /// <summary>Makes the bootstrap Owner a real one: own password, verified phone, forced change done.</summary>
    private static Task OwnerFinishesFirstLoginAsync(WebApplicationFactory<Program> host, string password) =>
        ChangeAsync(host, async (db, hasher) =>
        {
            var owner = await db.Accounts.SingleAsync(a => a.SubjectType == SubjectType.CooperativeOwner);
            owner.PasswordHash = hasher.Hash(password);
            owner.MustChangePassword = false;
            owner.TemporaryPasswordIssuedAt = null;
            owner.PhoneVerifiedAt = DateTimeOffset.UtcNow;
        });

    // H1. README: "for an **empty** database", yet also "run the demo seed on every new copy of a database". The gate
    // looks only for the well-known password, so a copy of real data (or a mistaken connection string) is seeded.
    [Fact]
    public async Task H1_the_shared_password_opens_no_real_data_on_a_database_that_is_not_empty()
    {
        var secret = AcsTestKit.NewPassword();
        var code = "REAL" + Random.Shared.Next(1000, 9999);
        await using var host = Host(new AuthApiFactory(), "Staging", "true", secret);
        await RunAsync(host, demo: false);
        await OwnerFinishesFirstLoginAsync(host, AcsTestKit.NewPassword());
        await ChangeAsync(host, (db, _) =>
        {
            db.FarmingZones.Add(new FarmingZone { Code = code, Name = "Vùng thật của khách hàng" });
            return Task.CompletedTask;
        });

        var outcome = await RunAsync(host, demo: true);

        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { identifier = "ketoan", password = secret });
        var visible = string.Empty;
        if (login.IsSuccessStatusCode)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
            visible = await (await client.GetAsync("/api/zones")).Content.ReadAsStringAsync();
        }

        using var scope = host.Services.CreateScope();
        var cooperative = await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Cooperatives.AsNoTracking().SingleAsync();
        Assert.False(
            visible.Contains(code),
            $"exit {outcome.ExitCode}; login 'ketoan' with the shared password {(int)login.StatusCode}; GET /api/zones shows the real zone {code}; " +
            $"the cooperative now has the demo tax code: {cooperative.TaxCode is not null}, a demo logo: {cooperative.LogoUrl is not null}");
    }

    // The profile step keeps a profile that has a tax code: the one thing of a real cooperative it does respect.
    [Fact]
    public async Task A_cooperative_with_a_tax_code_keeps_its_profile()
    {
        var taxCode = "0" + Random.Shared.Next(100000000, 999999999);
        await using var host = Host(new AuthApiFactory(), "Staging", "true", AcsTestKit.NewPassword());
        await RunAsync(host, demo: false);
        await ChangeAsync(host, async (db, _) => (await db.Cooperatives.SingleAsync()).TaxCode = taxCode);

        Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(host, demo: true)).ExitCode);

        using var scope = host.Services.CreateScope();
        Assert.Equal(taxCode, (await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Cooperatives.AsNoTracking().SingleAsync()).TaxCode);
    }

    // README: "The Owner can still reset a staff password", with no SMS. The new password goes to the Owner only.
    [Fact]
    public async Task The_owner_can_reset_a_staff_password_without_sms_and_the_new_password_stays_out_of_the_log()
    {
        var secret = AcsTestKit.NewPassword();
        var factory = new AuthApiFactory();
        await using var host = Host(factory, "Staging", "true", secret);
        await RunAsync(host, demo: true);
        var client = host.CreateClient();
        var tokens = await AcsTestKit.LoginTokensAsync(client, "owner", secret);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        Guid staffId;
        using (var scope = host.Services.CreateScope())
        {
            staffId = await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Accounts.Where(a => a.Username == "ketoan").Select(a => a.Id).SingleAsync();
        }

        var reset = await client.PostAsync($"/api/accounts/{staffId}/reset-password", null);

        var text = await reset.Content.ReadAsStringAsync();
        Assert.True(reset.StatusCode == HttpStatusCode.OK, text);
        var issued = JsonDocument.Parse(text).RootElement.GetProperty("temporaryPassword").GetString()!;
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains(issued));
        var login = await AcsTestKit.LoginTokensAsync(host.CreateClient(), "ketoan", issued);
        Assert.True(login.GetProperty("mustChangePassword").GetBoolean());
    }

    // The same cause: a username taken by a real account stops the seed half way, after it wrote to the real database.
    [Fact]
    public async Task H1_a_demo_run_that_fails_on_a_taken_username_leaves_no_demo_data_in_the_database()
    {
        var factory = new AuthApiFactory();
        await using var host = Host(factory, "Staging", "true", AcsTestKit.NewPassword());
        await RunAsync(host, demo: false);
        await OwnerFinishesFirstLoginAsync(host, AcsTestKit.NewPassword());
        await AddAccountNamedAsync(host, "ketoan");

        var failure = await Record.ExceptionAsync(() => RunAsync(host, demo: true));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var demoZones = await db.FarmingZones.CountAsync();
        var demoAccounts = await db.Accounts.CountAsync(a => a.Username == "quanlyvung1" || a.Username == "quanlyvung2");
        Assert.True(failure is not null, "the run did not fail");
        Assert.True(demoZones == 0 && demoAccounts == 0, $"after the failure: {demoZones} demo zones and {demoAccounts} demo logins stay in the database");
    }

    // H2. DemoOwnerStep "An Owner who already changed the password is left alone"; one who has not yet is overwritten,
    // on any database. The forced change and the phone step of BR-ACCOUNT-03 vanish with it, in Development this was
    // harmless, on a server others reach it hands the real Owner's account to whoever holds the shared password.
    [Theory]
    [InlineData("handed over, first login still to come")]
    [InlineData("password just reset with seed --reset-owner-password")]
    public async Task H2_an_owner_awaiting_a_first_login_keeps_their_password_and_their_forced_steps(string state)
    {
        var secret = AcsTestKit.NewPassword();
        await using var host = Host(new AuthApiFactory(), "Staging", "true", secret);
        await RunAsync(host, demo: false);
        if (state.StartsWith("password"))
        {
            await OwnerFinishesFirstLoginAsync(host, AcsTestKit.NewPassword());
            using var scope = host.Services.CreateScope();
            Assert.Equal(DeploymentBootstrapper.Success, (await scope.ServiceProvider.GetRequiredService<OwnerPasswordReset>().RunAsync(CancellationToken.None)).ExitCode);
        }

        var before = await OwnerAsync(host);
        Assert.True(Holds(host, before, BootstrapTests.OwnerTemporaryPassword));

        await RunAsync(host, demo: true);

        var after = await OwnerAsync(host);
        Assert.False(Holds(host, after, secret), "the Owner now logs in with the shared demo password");
        Assert.True(Holds(host, after, BootstrapTests.OwnerTemporaryPassword), "the Owner's own password no longer works");
        Assert.True(after.MustChangePassword, "the forced password change is gone");
        Assert.Equal(before.PhoneVerifiedAt, after.PhoneVerifiedAt);
    }

    // H2 on the real process: bootstrap as Production would, then the demo in Staging on that same database.
    [SqlServerFact]
    public async Task H2_on_the_real_process_a_production_handover_database_keeps_the_owner_password()
    {
        using var kit = new StagingDemoQaProcessKit();
        var connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        var secret = AcsTestKit.NewPassword();
        try
        {
            var settings = kit.ServerSettings(connection);
            var firstPassword = settings[StagingDemoQaProcessKit.EnvKey(BootstrapSettings.OwnerTemporaryPasswordKey)]!;
            settings["ASPNETCORE_ENVIRONMENT"] = "Production";
            Assert.Equal(0, (await kit.RunAsync(settings, "seed", "--bootstrap")).ExitCode);

            settings["ASPNETCORE_ENVIRONMENT"] = "Staging";
            settings["Demo__AllowOutsideDevelopment"] = "true";
            settings["Demo__Password"] = secret;
            var demo = await kit.RunAsync(settings, "seed", "--bootstrap", "--demo");
            using var api = await kit.StartApiAsync(settings);

            var withSecret = await api.Client.PostAsJsonAsync("/api/auth/login", new { identifier = "owner", password = secret });
            var withOwn = await api.Client.PostAsJsonAsync("/api/auth/login", new { identifier = "owner", password = firstPassword });
            Assert.True(
                withSecret.StatusCode != HttpStatusCode.OK && withOwn.StatusCode == HttpStatusCode.OK,
                $"demo exit {demo.ExitCode}; owner with the shared password {(int)withSecret.StatusCode}, with their own {(int)withOwn.StatusCode}");
        }
        finally
        {
            await StagingDemoQaProcessKit.DropDatabaseAsync(connection);
        }
    }
}
