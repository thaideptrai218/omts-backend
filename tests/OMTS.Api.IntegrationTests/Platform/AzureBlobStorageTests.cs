using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>The production provider (<c>Storage:Provider = AzureBlob</c>) against Azurite, switched by configuration only.</summary>
public class AzureBlobStorageTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    [AzuriteFact]
    public async Task Upload_signed_read_url_read_and_delete_work_against_blob_storage()
    {
        await using var api = BlobApi(Environment.GetEnvironmentVariable(AzuriteFactAttribute.VariableName)!);
        var storage = api.Services.GetRequiredService<IFileStorage>();

        var upload = await storage.UploadAsync(Validated(Png), "evidence");
        var url = await storage.GetReadUrlAsync(upload.Key!);
        using var http = new HttpClient();
        var download = await http.GetAsync(url);
        await using var read = await storage.OpenReadAsync(upload.Key!);
        using var copy = new MemoryStream();
        await read!.CopyToAsync(copy);

        Assert.True(upload.Succeeded);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/png", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal(Png, copy.ToArray());
        Assert.True(await storage.DeleteAsync(upload.Key!));
        Assert.Null(await storage.OpenReadAsync(upload.Key!));
        Assert.False(await storage.DeleteAsync(upload.Key!));
    }

    [AzuriteFact]
    public async Task Missing_key_reads_as_null()
    {
        await using var api = BlobApi(Environment.GetEnvironmentVariable(AzuriteFactAttribute.VariableName)!);
        var storage = api.Services.GetRequiredService<IFileStorage>();
        await storage.UploadAsync(Validated(Png), "evidence");

        Assert.Null(await storage.OpenReadAsync("evidence/2026/10/0123456789abcdef0123456789abcdef.png"));
    }

    // No account key (SAS-only connection string): read links cannot be signed, which is MSG05, not a 500.
    [Fact]
    public async Task Read_url_without_an_account_key_is_a_file_store_failure()
    {
        await using var api = BlobApi("BlobEndpoint=https://example.invalid/;SharedAccessSignature=sv=2024-01-01&sig=x");
        var storage = api.Services.GetRequiredService<IFileStorage>();

        await Assert.ThrowsAsync<FileStorageUnavailableException>(
            () => storage.GetReadUrlAsync("evidence/2026/10/0123456789abcdef0123456789abcdef.png"));
    }

    private static WebApplicationFactory<Program> BlobApi(string connectionString) =>
        new AuthApiFactory().WithWebHostBuilder(b => b
            .UseSetting("Storage:Provider", "AzureBlob")
            .UseSetting("Storage:AzureBlob:ConnectionString", connectionString)
            .UseSetting("Storage:AzureBlob:Container", $"omts-test-{Guid.NewGuid():N}"));

    private static ValidatedFile Validated(byte[] content)
    {
        var result = FileValidator.Validate([new IncomingFile("photo.png", content.Length, new MemoryStream(content))], FileRules.Photos);
        Assert.True(result.IsValid);
        return result.Files[0];
    }
}
