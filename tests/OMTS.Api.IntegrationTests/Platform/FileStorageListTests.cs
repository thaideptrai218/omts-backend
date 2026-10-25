using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>
/// <see cref="IFileStorage.ListAsync"/> of both providers (OMTS-214 orphan file clean-up): the files of one category
/// with their write time, nothing of other categories, nothing the provider did not write.
/// </summary>
public class FileStorageListTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    [Fact]
    public async Task Local_storage_lists_the_files_of_one_category()
    {
        using var factory = new AuthApiFactory();
        var storage = factory.Services.GetRequiredService<IFileStorage>();
        Assert.Empty(await storage.ListAsync(FileCategories.RaftPhotos));

        var (photos, licenseKey) = await UploadAsync(storage);
        await File.WriteAllTextAsync(Path.Combine(factory.StorageRoot, FileCategories.RaftPhotos, "notes.txt"), "not a stored file");
        var listed = await storage.ListAsync(FileCategories.RaftPhotos);

        AssertListed(photos, listed);
        Assert.Equal([licenseKey], (await storage.ListAsync(FileCategories.ZoneLicenses)).Select(f => f.Key));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.ListAsync("../raft-photos"));
    }

    [AzuriteFact]
    public async Task Blob_storage_lists_the_files_of_one_category()
    {
        await using var api = new AuthApiFactory().WithWebHostBuilder(b => b
            .UseSetting("Storage:Provider", "AzureBlob")
            .UseSetting("Storage:AzureBlob:ConnectionString", Environment.GetEnvironmentVariable(AzuriteFactAttribute.VariableName))
            .UseSetting("Storage:AzureBlob:Container", $"omts-test-{Guid.NewGuid():N}"));
        var storage = api.Services.GetRequiredService<IFileStorage>();
        // No container yet: nothing was ever uploaded.
        Assert.Empty(await storage.ListAsync(FileCategories.RaftPhotos));

        var (photos, licenseKey) = await UploadAsync(storage);
        var listed = await storage.ListAsync(FileCategories.RaftPhotos);

        AssertListed(photos, listed);
        Assert.Equal([licenseKey], (await storage.ListAsync(FileCategories.ZoneLicenses)).Select(f => f.Key));
        Assert.True(await storage.DeleteAsync(photos[0]));
        Assert.Equal([photos[1]], (await storage.ListAsync(FileCategories.RaftPhotos)).Select(f => f.Key));
    }

    private static async Task<(List<string> Photos, string License)> UploadAsync(IFileStorage storage)
    {
        var photos = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            photos.Add((await storage.UploadAsync(Validated(), FileCategories.RaftPhotos)).Key!);
        }

        // "raft-photos-old" shares the prefix text but is another category.
        await storage.UploadAsync(Validated(), "raft-photos-old");
        return (photos, (await storage.UploadAsync(Validated(), FileCategories.ZoneLicenses)).Key!);
    }

    private static void AssertListed(List<string> photos, IReadOnlyList<StoredFile> listed)
    {
        Assert.Equal(photos.Order(), listed.Select(f => f.Key).Order());
        // A real write time, not a default value; wide enough for an emulator clock a little off.
        Assert.All(listed, f => Assert.InRange(f.LastModified, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1)));
        Assert.All(listed, f => Assert.Equal(TimeSpan.Zero, f.LastModified.Offset));
    }

    private static ValidatedFile Validated()
    {
        var result = FileValidator.Validate([new IncomingFile("photo.png", Png.Length, new MemoryStream(Png))], FileRules.Photos);
        Assert.True(result.IsValid);
        return result.Files[0];
    }
}
