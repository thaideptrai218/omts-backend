using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using OMTS.Api.Controllers;
using OMTS.Api.Filters;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Bootstrap.Demo;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Platform.DemoOnSharedServerKit;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>QA of OMTS-216: the fake inbox on a server.</summary>
public partial class StagingDemoQaPasswordAndFakeTests
{
    // Change list: a fake SMS on a server "stays in memory and nobody can read it". It does stay, so look for every way out.
    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task The_fake_sms_inbox_fills_in_memory_and_no_response_log_or_delete_reaches_it(string environment)
    {
        var factory = new AuthApiFactory();
        await using var host = factory.WithWebHostBuilder(b => b
            .UseEnvironment(environment)
            .UseSetting("Sms:Provider", "Fake")
            .UseSetting("Ocr:Provider", ""));
        var client = host.CreateClient();
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
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        var sent = await client.PostAsJsonAsync("/api/me/phone-verification", new { });
        var responseText = await sent.Content.ReadAsStringAsync();

        var inbox = host.Services.GetRequiredService<FakeSmsSender>().Inbox;
        Assert.Contains(factory.Logs.Lines, line => line.Contains("SMS provider is Fake") && line.Contains("NOT sent"));
        var message = Assert.Single(inbox.List());
        var code = System.Text.RegularExpressions.Regex.Match(message.Text, @"\d{6}").Value;
        Assert.Equal(6, code.Length);
        Assert.DoesNotContain(code, responseText);
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains(code));
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains(message.Text));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/dev/sms")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/dev/sms")).StatusCode);
        Assert.Single(inbox.List());
    }

    // No second door: the inbox class is the only reader, and its filter sits on the class, so every action has it.
    [Fact]
    public void Only_the_filtered_dev_controller_exposes_fake_messages()
    {
        var api = typeof(DevInboxController).Assembly;
        Assert.NotNull(typeof(DevInboxController).GetCustomAttributes(typeof(DevelopmentOnlyAttribute), inherit: true).SingleOrDefault());

        var usesFake = api.GetTypes()
            .Where(t => t.IsAssignableTo(typeof(Microsoft.AspNetCore.Mvc.ControllerBase)))
            .Where(t => t.GetConstructors().SelectMany(c => c.GetParameters()).Any(p =>
                p.ParameterType == typeof(FakeSmsSender) || p.ParameterType == typeof(FakeEmailSender) || p.ParameterType == typeof(FakePushSender) || p.ParameterType == typeof(FakeMessageInbox)))
            .ToList();
        Assert.Equal([typeof(DevInboxController)], usesFake);
    }
}
