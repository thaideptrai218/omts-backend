using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Storage;

namespace OMTS.Api.IntegrationTests.Platform;

public class FileStorageTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    [Fact]
    public async Task Uploaded_file_gets_a_category_dated_key_without_the_client_file_name()
    {
        var result = await Storage.UploadAsync(new MemoryStream(Png), "Nguyen Van A raft.PNG", "image/png", "raft-photos");

        Assert.True(result.Succeeded);
        Assert.Matches(new Regex(@"^raft-photos/\d{4}/\d{2}/[a-f0-9]{32}\.png$"), result.Key);
    }

    [Fact]
    public async Task Signed_read_url_serves_the_original_bytes()
    {
        var key = (await Storage.UploadAsync(new MemoryStream(Png), "meter.png", "image/png", "meter-photos")).Key!;
        var url = await Storage.GetReadUrlAsync(key);

        var response = await factory.CreateClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Tampered_or_expired_url_is_refused()
    {
        var key = (await Storage.UploadAsync(new MemoryStream(Png), "meter.png", "image/png", "meter-photos")).Key!;
        var client = factory.CreateClient();
        var valid = await Storage.GetReadUrlAsync(key);
        var expired = await Storage.GetReadUrlAsync(key, TimeSpan.FromMinutes(-1));
        var otherKey = valid.Replace(key, key.Replace("meter-photos", "raft-photos"));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(valid + "0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(expired)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(otherKey)).StatusCode);
    }

    [Fact]
    public async Task Deleted_file_can_no_longer_be_read()
    {
        var key = (await Storage.UploadAsync(new MemoryStream(Png), "a.png", "image/png", "evidence")).Key!;

        Assert.True(await Storage.DeleteAsync(key));
        Assert.Null(await Storage.OpenReadAsync(key));
        Assert.False(await Storage.DeleteAsync(key));
    }

    private IFileStorage Storage => factory.Services.GetRequiredService<IFileStorage>();
}
