using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Features.Authorization;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>Review follow-ups of OMTS-200: probes, providers outside Development, file store failures.</summary>
public class PlatformHardeningTests
{
    // /health/ready checks the fixed role rows by code, so a renamed row stays green; only a missing row turns it red.
    // Platform probes use /health/db, which even that cannot turn red.
    [Fact]
    public async Task Readiness_finds_the_fixed_roles_by_code_and_the_database_probe_stays_green_without_one()
    {
        await using var api = BootstrapTests.WithSecrets(new AuthApiFactory());
        using (var scope = api.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(false, CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
            (await db.Roles.SingleAsync(r => r.Code == FixedRoles.ZoneManagerCode)).Name = "Zone Lead";
            await db.SaveChangesAsync();
        }

        var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
            db.Roles.Remove(await db.Roles.SingleAsync(r => r.Code == FixedRoles.AccountantCode));
            await db.SaveChangesAsync();
        }

        var ready = await client.GetAsync("/health/ready");
        var database = await client.GetAsync("/health/db");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Contains("default-roles", (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("failing").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(HttpStatusCode.OK, database.StatusCode);
        Assert.Empty((await database.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("failing").EnumerateArray());
    }

    // Outside Development no fake pretends a verification code or a push was delivered.
    [Fact]
    public async Task Sms_and_push_are_not_sent_and_not_faked_outside_development()
    {
        await using var development = new AuthApiFactory();
        await using var production = new AuthApiFactory().WithWebHostBuilder(b => b.UseEnvironment("Production"));
        production.CreateClient();

        var sms = await production.Services.GetRequiredService<ISmsSender>().SendAsync("0912000222", "code 123456");
        var push = await production.Services.GetRequiredService<IPushSender>().SendAsync(
            new PushMessage(Guid.NewGuid(), "Test", "Title", null, null, null, []));

        Assert.False(sms.Succeeded);
        Assert.Equal("NOT_CONFIGURED", sms.ErrorCode);
        Assert.False(push.Succeeded);
        Assert.Equal("NOT_CONFIGURED", push.ErrorCode);
        Assert.IsType<FakeSmsSender>(development.Services.GetRequiredService<ISmsSender>());
        Assert.IsType<FakePushSender>(development.Services.GetRequiredService<IPushSender>());
    }

    [Fact]
    public async Task File_store_read_failure_answers_502_MSG05_without_details()
    {
        await using var api = new AuthApiFactory();

        var response = await api.CreateClient().GetAsync("/test-only/storage-read-failure");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("MSG05", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("evidence/", body);
    }
}
