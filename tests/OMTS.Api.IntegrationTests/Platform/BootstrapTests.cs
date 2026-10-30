using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>One-time deployment command and the readiness check (OMTS-200), on a fresh database per test.</summary>
public class BootstrapTests
{
    private const string TemporaryPassword = "Temp2026ab";

    /// <summary>The Owner's temporary password that <see cref="WithSecrets"/> configures.</summary>
    internal static string OwnerTemporaryPassword => TemporaryPassword;

    [Fact]
    public async Task Readiness_is_red_before_the_bootstrap_and_green_after_it()
    {
        await using var api = WithSecrets(new AuthApiFactory());
        var client = api.CreateClient();

        var before = await client.GetAsync("/health/ready");
        var failing = (await before.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("failing").EnumerateArray().Select(e => e.GetString()).ToList();
        var outcome = await RunAsync(api);
        var after = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, before.StatusCode);
        Assert.Contains("cooperative", failing);
        Assert.Contains("cooperative-owner", failing);
        Assert.Contains("default-roles", failing);
        Assert.Equal(DeploymentBootstrapper.Success, outcome.ExitCode);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Running_twice_creates_one_cooperative_one_owner_and_the_two_default_roles()
    {
        var host = new AuthApiFactory();
        await using var api = WithSecrets(host);

        await RunAsync(api);
        var second = await RunAsync(api);

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        Assert.Equal(DeploymentBootstrapper.Success, second.ExitCode);
        Assert.Contains(second.Messages, m => m.Contains("already initialized"));
        Assert.Equal(1, await db.Cooperatives.CountAsync());
        Assert.Equal(1, await db.Accounts.CountAsync(a => a.SubjectType == SubjectType.CooperativeOwner));
        var roleNames = DefaultRoles.All.Select(r => r.Name).ToList();
        Assert.Equal(2, await db.Roles.CountAsync(r => roleNames.Contains(r.Name)));

        var audit = await db.AuditEvents.Where(e => e.Action == "Cooperative.Created" || e.Action == "Account.Created").ToListAsync();
        Assert.Equal(2, audit.Count);
        Assert.All(audit, e => Assert.Null(e.ActorAccountId));
        Assert.All(audit, e => Assert.DoesNotContain(TemporaryPassword, e.After ?? string.Empty));
        Assert.DoesNotContain(second.Messages.Concat(host.Logs.Lines), line => line.Contains(TemporaryPassword));
    }

    [Fact]
    public async Task Owner_logs_in_with_the_temporary_password_and_must_change_it()
    {
        await using var api = WithSecrets(new AuthApiFactory());
        await RunAsync(api);
        var client = api.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { identifier = "owner", password = TemporaryPassword });
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        var blocked = await client.GetAsync("/api/me/permissions");

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(tokens.GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

        // Once the password is changed (ACS-04), the Owner holds every catalog function everywhere.
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
            var owner = await db.Accounts.SingleAsync(a => a.Username == "owner");
            owner.MustChangePassword = false;
            await db.SaveChangesAsync();
        }

        var permissions = await client.GetFromJsonAsync<JsonElement>("/api/me/permissions");
        var functions = permissions.GetProperty("functions").EnumerateArray().ToList();
        Assert.Equal(PermissionFunctions.All.Count, functions.Count);
        Assert.All(functions, f => Assert.True(f.GetProperty("allZones").GetBoolean()));
    }

    [Fact]
    public async Task Missing_secrets_exit_with_1_naming_the_keys_and_create_nothing()
    {
        await using var api = new AuthApiFactory().WithWebHostBuilder(b => b
            .UseSetting(BootstrapSettings.CooperativeFullNameKey, "HTX Trung Nam"));

        var outcome = await RunAsync(api);

        Assert.Equal(DeploymentBootstrapper.Failure, outcome.ExitCode);
        var message = Assert.Single(outcome.Messages, m => m.StartsWith("Missing bootstrap secrets"));
        Assert.Contains(BootstrapSettings.OwnerUsernameKey, message);
        Assert.Contains(BootstrapSettings.OwnerTemporaryPasswordKey, message);
        Assert.DoesNotContain(BootstrapSettings.CooperativeFullNameKey, message);
        using var scope = api.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Cooperatives.AnyAsync());
    }

    [Fact]
    public async Task Demo_zones_are_seeded_once_in_development_and_refused_in_production()
    {
        await using var development = WithSecrets(new AuthApiFactory());
        await using var production = WithSecrets(new AuthApiFactory()).WithWebHostBuilder(b => b.UseEnvironment("Production").UseSetting("Ocr:Provider", ""));

        await RunAsync(development, demo: true);
        await RunAsync(development, demo: true);
        var refused = await RunAsync(production, demo: true);

        using var scope = development.Services.CreateScope();
        var codes = await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().FarmingZones.Select(z => z.Code).ToListAsync();
        // Two In Use zones and one Discontinued zone; the second run created nothing more.
        Assert.Equal(["DEMO-01", "DEMO-02", "DEMO-03"], codes.Order());
        Assert.Equal(DeploymentBootstrapper.Failure, refused.ExitCode);
    }

    internal static WebApplicationFactory<Program> WithSecrets(AuthApiFactory api) => api.WithWebHostBuilder(b => b
        .UseSetting(BootstrapSettings.CooperativeFullNameKey, "HTX Nuôi trồng thủy sản Trung Nam")
        .UseSetting(BootstrapSettings.OwnerDisplayNameKey, "Chủ nhiệm HTX")
        .UseSetting(BootstrapSettings.OwnerUsernameKey, "owner")
        .UseSetting(BootstrapSettings.OwnerPhoneNumberKey, "+84912345678")
        .UseSetting(BootstrapSettings.OwnerTemporaryPasswordKey, TemporaryPassword));

    private static async Task<BootstrapOutcome> RunAsync(WebApplicationFactory<Program> api, bool demo = false)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(demo, CancellationToken.None);
    }
}
