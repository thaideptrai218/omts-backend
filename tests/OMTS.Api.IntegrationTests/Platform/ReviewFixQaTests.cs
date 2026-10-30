using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files.Models;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>QA checks of the OMTS-200 review fixes: real endpoints, provider refusals, probes, bootstrap re-run.</summary>
public class ReviewFixQaTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    // A read that fails in the file store reaches the client as 502 MSG05 through the real download endpoint.
    // The test holds the file open exclusively so File.OpenRead throws (a sharing violation), not a missing file.
    [Fact]
    public async Task Local_download_whose_file_cannot_be_read_answers_502_MSG05_without_paths()
    {
        await using var api = new AuthApiFactory();
        var storage = api.Services.GetRequiredService<IFileStorage>();
        var key = (await storage.UploadAsync(Validated(Png), "meter-photos")).Key!;
        var url = await storage.GetReadUrlAsync(key);
        using var locked = new FileStream(Path.GetFullPath(Path.Combine(api.StorageRoot, key)), FileMode.Open, FileAccess.Read, FileShare.None);

        var response = await api.CreateClient().GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("MSG05", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(api.StorageRoot, body);
        Assert.DoesNotContain(key, body);
    }

    // A blob store that cannot be reached is a file store failure (502 MSG05), not an unhandled exception.
    [Fact]
    public async Task Unreachable_blob_store_fails_the_read_as_a_file_store_failure()
    {
        var accountKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        await using var api = BlobApi(
            $"DefaultEndpointsProtocol=http;AccountName=qaaccount;AccountKey={accountKey};BlobEndpoint=http://127.0.0.1:1/qaaccount");
        var storage = api.Services.GetRequiredService<IFileStorage>();

        await Assert.ThrowsAsync<FileStorageUnavailableException>(
            () => storage.OpenReadAsync("evidence/2026/10/0123456789abcdef0123456789abcdef.png"));
    }

    // Plain SMTP is refused outside Development, and only there: a clear-text login must not reach production.
    [Fact]
    public void Smtp_security_none_stops_the_host_in_production_when_smtp_is_the_provider()
    {
        using var production = new AuthApiFactory().WithWebHostBuilder(b => b
            .UseEnvironment("Production")
            .UseSetting("Email:Provider", "Smtp")
            .UseSetting("Email:Smtp:Security", "None"));

        var error = Assert.ThrowsAny<InvalidOperationException>(() => production.CreateClient());
        Assert.Contains("Security", error.Message);
    }

    [Theory]
    [InlineData("StartTls")]
    [InlineData("SslOnConnect")]
    public async Task Encrypted_smtp_security_modes_start_in_production(string security)
    {
        await using var production = new AuthApiFactory().WithWebHostBuilder(b => b
            .UseEnvironment("Production")
            .UseSetting("Email:Provider", "Smtp")
            .UseSetting("Email:Smtp:Host", "smtp.example.invalid")
            .UseSetting("Email:Smtp:Security", security));

        var response = await production.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Plain_smtp_is_allowed_in_development_for_local_test_servers()
    {
        await using var development = new AuthApiFactory().WithWebHostBuilder(b => b
            .UseEnvironment("Development")
            .UseSetting("Email:Provider", "Smtp")
            .UseSetting("Email:Smtp:Security", "None"));

        var response = await development.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // A rejected recipient makes the SMTP server echo the address; the log must keep the masked form only.
    [Fact]
    public async Task Rejected_recipient_is_logged_without_the_address_and_returns_a_failed_result()
    {
        var (port, received, server) = StartRejectingSmtpServer();
        await using var factory = new AuthApiFactory();
        await using var api = factory.WithWebHostBuilder(b => b
            .UseEnvironment("Development")
            .UseSetting("Email:Provider", "Smtp")
            .UseSetting("Email:Smtp:Host", "127.0.0.1")
            .UseSetting("Email:Smtp:Port", port.ToString())
            .UseSetting("Email:Smtp:Security", "None")
            .UseSetting("Email:Smtp:FromAddress", "noreply@example.com")
            .UseSetting("Email:Smtp:TimeoutSeconds", "5"));
        api.CreateClient();
        var sender = api.Services.GetRequiredService<IEmailSender>();

        var result = await sender.SendAsync("qa.person@example.com", "Subject", "<p>body</p>", "body");
        await server;

        Assert.Contains(received, line => line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase));
        Assert.False(result.Succeeded);
        Assert.Equal("SMTP_ERROR", result.ErrorCode);
        Assert.Contains(factory.Logs.Lines, line => line.Contains("Email to"));
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains("qa.person@example.com"));
    }

    // The database probe must not depend on business data: locking the only Owner turns /health/ready red only.
    [Fact]
    public async Task Database_probe_stays_green_when_the_only_owner_is_locked()
    {
        await using var api = BootstrapTests.WithSecrets(new AuthApiFactory());
        using (var scope = api.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(false, CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
            (await db.Accounts.SingleAsync(a => a.SubjectType == SubjectType.CooperativeOwner)).StatusId = StatusIds.Account.Locked;
            await db.SaveChangesAsync();
        }

        var client = api.CreateClient();
        var ready = await client.GetAsync("/health/ready");
        var database = await client.GetAsync("/health/db");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Contains("cooperative-owner", (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("failing").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(HttpStatusCode.OK, database.StatusCode);
    }

    // README (after the S3 fix): a re-run recreates a default role whose name no longer exists, creates nothing else.
    [Fact]
    public async Task Bootstrap_rerun_after_a_role_rename_recreates_only_the_missing_default_role()
    {
        await using var api = BootstrapTests.WithSecrets(new AuthApiFactory());
        await RunBootstrapAsync(api);
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
            (await db.Roles.SingleAsync(r => r.Name == "Zone Manager")).Name = "Zone Lead";
            await db.SaveChangesAsync();
        }

        var outcome = await RunBootstrapAsync(api);

        using var check = api.Services.CreateScope();
        var verify = check.ServiceProvider.GetRequiredService<OmtsDbContext>();
        Assert.Equal(DeploymentBootstrapper.Success, outcome.ExitCode);
        Assert.Equal(1, await verify.Roles.CountAsync(r => r.Name == "Zone Manager"));
        Assert.Equal(1, await verify.Roles.CountAsync(r => r.Name == "Zone Lead"));
        Assert.Equal(1, await verify.Cooperatives.CountAsync());
        Assert.Equal(1, await verify.Accounts.CountAsync(a => a.SubjectType == SubjectType.CooperativeOwner));
    }

    private static async Task<BootstrapOutcome> RunBootstrapAsync(WebApplicationFactory<Program> api)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(false, CancellationToken.None);
    }

    private static WebApplicationFactory<Program> BlobApi(string connectionString) =>
        new AuthApiFactory().WithWebHostBuilder(b => b
            .UseSetting("Storage:Provider", "AzureBlob")
            .UseSetting("Storage:AzureBlob:ConnectionString", connectionString)
            .UseSetting("Storage:AzureBlob:Container", $"omts-qa-{Guid.NewGuid():N}"));

    private static ValidatedFile Validated(byte[] content)
    {
        var result = FileValidator.Validate([new IncomingFile("photo.png", content.Length, new MemoryStream(content))], FileRules.Photos);
        Assert.True(result.IsValid);
        return result.Files[0];
    }

    // Minimal SMTP server: accepts the session, refuses the recipient with a reply that echoes the address.
    private static (int Port, ConcurrentQueue<string> Received, Task Done) StartRejectingSmtpServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new ConcurrentQueue<string>();
        var done = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 localhost ESMTP ready");
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    received.Enqueue(line);
                    if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250 localhost");
                    }
                    else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250 OK");
                    }
                    else if (line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync($"550 5.1.1 {line[8..]}: Recipient address rejected: User unknown");
                    }
                    else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("221 bye");
                        break;
                    }
                    else
                    {
                        await writer.WriteLineAsync("502 not implemented");
                    }
                }
            }
            finally
            {
                listener.Stop();
            }
        });
        return (port, received, done);
    }
}
