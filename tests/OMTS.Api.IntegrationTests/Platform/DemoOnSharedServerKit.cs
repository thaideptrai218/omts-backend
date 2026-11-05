using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Models;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Bootstrap.Demo;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>Hosts configured like a shared demo server, and what every demo login must then do.</summary>
internal static class DemoOnSharedServerKit
{
    private static readonly string[] DemoAccounts =
        ["ketoan", "nhanvien.chuaxacthuc", "nhanvien.doimk", "nhanvien.hethan", "owner", "quanlyvung1", "quanlyvung2", "thuonglai1", "thuonglai2"];

    public static WebApplicationFactory<Program> Host(AuthApiFactory factory, string environment, string flag, string? password) =>
        BootstrapTests.WithSecrets(factory).WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment)
                // The test host loads appsettings.Development.json whatever the environment; these are the shared-server values.
                .UseSetting("Ocr:Provider", "")
                .UseSetting("Sms:Provider", "None")
                .UseSetting("Push:Provider", "None")
                .UseSetting(DemoSeedGate.AllowOutsideDevelopmentKey, flag);
            if (password is not null)
            {
                b.UseSetting(DemoSeedGate.PasswordKey, password);
            }
        });

    public static WebApplicationFactory<Program> OnSqlServer(SqlServerTestDatabase database, string storageRoot, string password, string environment = "Staging") =>
        new SqlServerApiFactory(database, new Dictionary<string, string?>
        {
            [BootstrapSettings.CooperativeFullNameKey] = "Hợp tác xã Nuôi hàu Vân Đồn",
            [BootstrapSettings.OwnerDisplayNameKey] = "Nguyễn Văn Hải",
            [BootstrapSettings.OwnerUsernameKey] = "owner",
            [BootstrapSettings.OwnerPhoneNumberKey] = "0900000001",
            [BootstrapSettings.OwnerTemporaryPasswordKey] = AcsTestKit.NewPassword(),
            ["Storage:Local:RootPath"] = storageRoot,
            ["Storage:Local:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
            ["Ocr:Provider"] = "",
            ["Sms:Provider"] = "None",
            ["Push:Provider"] = "None",
            [DemoSeedGate.AllowOutsideDevelopmentKey] = "true",
            [DemoSeedGate.PasswordKey] = password,
        }).WithWebHostBuilder(b => b.UseEnvironment(environment));

    public static async Task<BootstrapOutcome> RunAsync(WebApplicationFactory<Program> api, bool demo)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(demo, CancellationToken.None);
    }

    /// <summary>
    /// Every account holds this password and not the well-known one, and lands in the branch the README promises, without
    /// any SMS: only the account meant to be unverified still has a phone to verify, and that cannot complete here.
    /// </summary>
    public static async Task AssertEveryDemoLoginUsesAsync(WebApplicationFactory<Program> api, string password)
    {
        using (var scope = api.Services.CreateScope())
        {
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var accounts = await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Accounts.ToListAsync();
            Assert.Equal(DemoAccounts, accounts.Select(a => a.Username).Order());
            Assert.All(accounts, a => Assert.NotEqual(PasswordCheck.Failed, hasher.Verify(a.PasswordHash, password)));
            Assert.All(accounts, a => Assert.Equal(PasswordCheck.Failed, hasher.Verify(a.PasswordHash, DemoSeedGate.WellKnownPassword)));
        }

        var client = api.CreateClient();
        async Task<(HttpStatusCode Status, JsonElement Body)> LoginAsync(string username)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new { identifier = username, password });
            return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
        }

        foreach (var username in new[] { "owner", "quanlyvung1", "quanlyvung2", "ketoan", "thuonglai1" })
        {
            var (status, body) = await LoginAsync(username);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.False(body.GetProperty("mustChangePassword").GetBoolean(), username);
            Assert.False(body.GetProperty("phoneVerificationRequired").GetBoolean(), username);
        }

        foreach (var username in new[] { "thuonglai2", "nhanvien.hethan" })
        {
            var (status, body) = await LoginAsync(username);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal("MSG-ACCOUNT-07", body.GetProperty("code").GetString());
        }

        var mustChange = await LoginAsync("nhanvien.doimk");
        Assert.Equal(HttpStatusCode.OK, mustChange.Status);
        Assert.True(mustChange.Body.GetProperty("mustChangePassword").GetBoolean());
        Assert.False(mustChange.Body.GetProperty("phoneVerificationRequired").GetBoolean());

        var unverified = await LoginAsync("nhanvien.chuaxacthuc");
        Assert.Equal(HttpStatusCode.OK, unverified.Status);
        Assert.True(unverified.Body.GetProperty("phoneVerificationRequired").GetBoolean());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", unverified.Body.GetProperty("accessToken").GetString());
        var gated = await client.GetAsync("/api/me/permissions");
        Assert.Equal(HttpStatusCode.Forbidden, gated.StatusCode);
        Assert.Equal("MSG-ACCOUNT-04", (await gated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var codeRequest = await client.PostAsJsonAsync("/api/me/phone-verification", new { });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, codeRequest.StatusCode);
        Assert.Equal("MSG-ACCOUNT-13", (await codeRequest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
