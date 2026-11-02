using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files.Models;
using OMTS.Application.Features.Files;

namespace OMTS.Api.IntegrationTests.Platform;

public class FileStorageTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    [Fact]
    public async Task Uploaded_file_gets_a_category_dated_key_without_the_client_file_name()
    {
        var result = await Storage.UploadAsync(Validated("Nguyen Van A raft.PNG", Png), "raft-photos");

        Assert.True(result.Succeeded);
        Assert.Matches(new Regex(@"^raft-photos/\d{4}/\d{2}/[a-f0-9]{32}\.png$"), result.Key);
    }

    // A file named .html that starts like a PDF is stored and served as a PDF, never as HTML.
    [Fact]
    public async Task Disguised_html_is_stored_and_served_as_its_real_format()
    {
        var content = "%PDF-1.4<script>alert(1)</script>"u8.ToArray();

        var key = (await Storage.UploadAsync(Validated("license.html", content), "zone-licenses")).Key!;
        var response = await factory.CreateClient().GetAsync(await Storage.GetReadUrlAsync(key));

        Assert.EndsWith(".pdf", key);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Signed_read_url_serves_the_original_bytes()
    {
        var key = (await Storage.UploadAsync(Validated("meter.png", Png), "meter-photos")).Key!;

        var response = await factory.CreateClient().GetAsync(await Storage.GetReadUrlAsync(key));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Tampered_expired_or_borrowed_signatures_are_refused()
    {
        var first = (await Storage.UploadAsync(Validated("a.png", Png), "meter-photos")).Key!;
        var second = (await Storage.UploadAsync(Validated("b.png", Png), "raft-photos")).Key!;
        var client = factory.CreateClient();
        var valid = await Storage.GetReadUrlAsync(first);
        var expired = await Storage.GetReadUrlAsync(first, TimeSpan.FromMinutes(-1));
        // The signature of the first file on the (existing) second file.
        var borrowed = valid.Replace(first, second);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(valid)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(valid + "0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(expired)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(borrowed)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/files/local/{first}?expires={long.MaxValue}&sig=00")).StatusCode);
    }

    [Fact]
    public async Task Deleted_file_can_no_longer_be_read()
    {
        var key = (await Storage.UploadAsync(Validated("a.png", Png), "evidence")).Key!;

        Assert.True(await Storage.DeleteAsync(key));
        Assert.Null(await Storage.OpenReadAsync(key));
        Assert.False(await Storage.DeleteAsync(key));
    }

    // Configuration errors stop the host at startup instead of failing a user request later.
    [Theory]
    [InlineData("Storage:Local:SigningKey", "")]
    [InlineData("Sms:Provider", "Twilio")]
    [InlineData("Storage:Provider", "Dropbox")]
    [InlineData("Email:Smtp:Security", "None")]
    public void Invalid_storage_or_messaging_configuration_stops_the_host_in_production(string key, string value)
    {
        // The test host forces the fake OCR, which is itself refused in Production: unset it so only `key` can stop the host.
        using var production = factory.WithWebHostBuilder(b => b.UseEnvironment("Production").UseSetting("Ocr:Provider", "").UseSetting(key, value));

        Assert.ThrowsAny<InvalidOperationException>(() => production.CreateClient());
    }

    [Fact]
    public void The_fake_ocr_provider_stops_the_host_outside_development()
    {
        using var production = factory.WithWebHostBuilder(b => b.UseEnvironment("Production").UseSetting("Ocr:Provider", "Fake"));

        var failure = Assert.ThrowsAny<InvalidOperationException>(() => production.CreateClient());

        Assert.Contains("Ocr:Provider=Fake", failure.Message);
    }

    private IFileStorage Storage => factory.Services.GetRequiredService<IFileStorage>();

    private static ValidatedFile Validated(string fileName, byte[] content)
    {
        var result = FileValidator.Validate([new IncomingFile(fileName, content.Length, new MemoryStream(content))], FileRules.Documents);
        Assert.True(result.IsValid);
        return result.Files[0];
    }
}
