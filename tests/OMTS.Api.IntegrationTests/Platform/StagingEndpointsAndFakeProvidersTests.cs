using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap.Demo;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>
/// A shared demo server (council decision 4, OMTS-216): the dev inbox and Swagger stay closed whatever the demo flag
/// says, and what the fake providers really do there.
/// </summary>
public class StagingEndpointsAndFakeProvidersTests
{
    private static readonly string[] DevAndSwaggerPaths =
        ["/api/dev/sms", "/api/dev/email", "/api/dev/push", "/swagger", "/swagger/index.html", "/swagger/v1/swagger.json"];

    // Production is here to show the flag widens nothing there either.
    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Dev_inboxes_and_swagger_answer_404_with_the_demo_flag_on_and_every_fake_provider_set(string environment)
    {
        await using var host = Host(new AuthApiFactory(), environment, sms: "Fake", email: "Fake", push: "Fake");
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var path in DevAndSwaggerPaths)
        {
            Assert.True((await client.GetAsync(path)).StatusCode == HttpStatusCode.NotFound, path);
        }

        foreach (var path in DevAndSwaggerPaths.Where(p => p.StartsWith("/api/dev/")))
        {
            Assert.True((await client.DeleteAsync(path)).StatusCode == HttpStatusCode.NotFound, path);
        }
    }

    // The same requests do reach something in Development, so the 404s above are not a wrong address.
    [Fact]
    public async Task Dev_inboxes_and_swagger_still_answer_in_development()
    {
        await using var development = new AuthApiFactory();
        var client = development.CreateClient();

        foreach (var path in DevAndSwaggerPaths.Where(p => p != "/swagger"))
        {
            Assert.True((await client.GetAsync(path)).StatusCode == HttpStatusCode.OK, path);
        }
    }

    // Fakes are not refused outside Development: the host starts and warns. They answer as if the message left, and the
    // code stays in a memory nobody can read, so the fake SMS is a dead end on a shared server. None says so to the client.
    [Fact]
    public async Task Fake_providers_in_staging_only_warn_and_a_code_sent_by_the_fake_sms_cannot_be_read()
    {
        var factory = new AuthApiFactory();
        await using var host = Host(factory, "Staging", sms: "Fake", email: "Fake", push: "Fake");
        var client = host.CreateClient();
        Assert.IsType<FakeSmsSender>(host.Services.GetRequiredService<ISmsSender>());

        Assert.Contains(factory.Logs.Lines, line => line.Contains("SMS provider is Fake") && line.Contains("NOT sent"));
        Assert.Contains(factory.Logs.Lines, line => line.Contains("Push provider is Fake") && line.Contains("NOT sent"));
        Assert.Contains(factory.Logs.Lines, line => line.Contains("Email provider is Fake") && line.Contains("NOT sent"));

        var response = await SendPhoneCodeAsync(host, client);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/dev/sms")).StatusCode);
    }

    [Fact]
    public async Task The_default_none_sms_in_staging_tells_the_client_the_code_could_not_be_sent()
    {
        var factory = new AuthApiFactory();
        await using var host = Host(factory, "Staging", sms: "None", email: "Smtp", push: "None");
        var client = host.CreateClient();

        var response = await SendPhoneCodeAsync(host, client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(factory.Logs.Lines, line => line.Contains("SMS provider is None") && line.Contains("NOT sent"));
    }

    // The fake OCR invents readings, so it stops the host outside Development, flag or no flag.
    [Fact]
    public void Fake_ocr_stops_the_host_in_staging_with_the_demo_flag_on()
    {
        using var host = Host(new AuthApiFactory(), "Staging", sms: "None", email: "Smtp", push: "None", ocr: "Fake");

        var error = Assert.ThrowsAny<InvalidOperationException>(() => host.CreateClient());
        Assert.Contains("Ocr:Provider=Fake", error.Message);
    }

    private static WebApplicationFactory<Program> Host(AuthApiFactory factory, string environment, string sms, string email, string push, string ocr = "") =>
        factory.WithWebHostBuilder(b => b
            .UseEnvironment(environment)
            .UseSetting("Sms:Provider", sms)
            .UseSetting("Email:Provider", email)
            .UseSetting("Push:Provider", push)
            .UseSetting("Ocr:Provider", ocr)
            .UseSetting(DemoSeedGate.AllowOutsideDevelopmentKey, "true")
            .UseSetting(DemoSeedGate.PasswordKey, AcsTestKit.NewPassword()));

    /// <summary>Logs in an account with an unverified phone and asks for its code (ACCOUNT-02.1).</summary>
    private static async Task<HttpResponseMessage> SendPhoneCodeAsync(WebApplicationFactory<Program> host, HttpClient client)
    {
        var username = AcsTestKit.NewCode();
        var password = AcsTestKit.NewPassword();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
            db.Accounts.Add(new Account
            {
                Username = username,
                DisplayName = username,
                PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(password),
                SubjectType = SubjectType.CooperativeOwner,
                PhoneNumber = AcsTestKit.NewPhone(),
                StatusId = StatusIds.Account.Active,
            });
            await db.SaveChangesAsync();
        }

        var tokens = await AcsTestKit.LoginTokensAsync(client, username, password);
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.GetProperty("accessToken").GetString());
        return await client.PostAsJsonAsync("/api/me/phone-verification", new { });
    }
}
