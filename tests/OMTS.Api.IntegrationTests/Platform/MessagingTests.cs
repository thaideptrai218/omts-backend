using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Messaging;

namespace OMTS.Api.IntegrationTests.Platform;

public class MessagingTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string CodeText = "Your OMTS verification code is 482913";

    [Fact]
    public async Task Fake_sms_reaches_the_dev_inbox_but_never_the_logs()
    {
        var phone = "0912000111";
        var result = await factory.Services.GetRequiredService<ISmsSender>().SendAsync(phone, CodeText);
        var client = factory.CreateClient();

        var inbox = await client.GetFromJsonAsync<JsonElement>($"/api/dev/sms?phoneNumber={phone}");

        Assert.True(result.Succeeded);
        Assert.Equal(CodeText, inbox[0].GetProperty("text").GetString());
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains("482913"));
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains(phone));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/dev/sms")).StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>($"/api/dev/sms?phoneNumber={phone}")).GetArrayLength());
    }

    [Fact]
    public async Task Configured_numbers_get_a_failed_result_instead_of_an_exception()
    {
        await using var failing = factory.WithWebHostBuilder(b => b.UseSetting("Sms:Fake:FailFor:0", "0912999888"));

        var result = await failing.Services.GetRequiredService<ISmsSender>().SendAsync("0912999888", CodeText);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.ErrorCode);
    }

    [Fact]
    public async Task Unreachable_smtp_server_gives_a_failed_result()
    {
        await using var smtp = factory.WithWebHostBuilder(b => b
            .UseSetting("Email:Provider", "Smtp")
            .UseSetting("Email:Smtp:Host", "127.0.0.1")
            .UseSetting("Email:Smtp:Port", "1")
            .UseSetting("Email:Smtp:FromAddress", "omts@example.com")
            .UseSetting("Email:Smtp:TimeoutSeconds", "5"));

        var result = await smtp.Services.GetRequiredService<IEmailSender>()
            .SendAsync("owner@example.com", "Code", "<p>code</p>", "code");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Dev_inboxes_do_not_exist_outside_development()
    {
        await using var production = factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var client = production.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/dev/sms")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/dev/push")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/dev/email")).StatusCode);
    }
}
