using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cooperatives;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// Orphan file clean-up (OMTS-214): only raft photos and license files that no record points to and that are older
/// than the grace period are deleted; cooperative logos and files the storage did not write are never touched.
/// </summary>
public class OrphanFileCleanupTests
{
    [Fact]
    public async Task Deletes_only_old_unreferenced_raft_photos_and_license_files_and_never_logos()
    {
        using var factory = new AuthApiFactory();
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var raftId = (await owner.Client.CreateRaftAsync(zoneId)).Id();
        await (await owner.Client.PostAsync($"/api/rafts/{raftId}/photo", new MultipartFormDataContent { { FileContent(Jpeg), "file", "raft.jpg" } }))
            .ReadAsync(HttpStatusCode.OK);
        await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneIds = [zoneId], EffectiveDate = Today });
        // The first logo is replaced: no record points to it any more, but published versions may still show it.
        await (await CooperativeProfileRequests.UploadLogoAsync(owner.Client, Png, "logo.png")).ReadAsync(HttpStatusCode.OK);
        await (await CooperativeProfileRequests.UploadLogoAsync(owner.Client, Jpeg, "logo.jpg")).ReadAsync(HttpStatusCode.OK);
        var storage = factory.Services.GetRequiredService<IFileStorage>();
        var orphanPhoto = await StoreAsync(storage, FileCategories.RaftPhotos, Png);
        var orphanLicense = await StoreAsync(storage, FileCategories.ZoneLicenses, Pdf);
        await File.WriteAllTextAsync(Path.Combine(factory.StorageRoot, FileCategories.RaftPhotos, "notes.txt"), "not a stored file");
        var aged = AllKeys(factory);
        foreach (var key in aged)
        {
            File.SetLastWriteTimeUtc(Path.Combine(factory.StorageRoot, key), DateTime.UtcNow.AddDays(-2));
        }

        // Written just now, like an upload still waiting for its save.
        var fresh = await StoreAsync(storage, FileCategories.RaftPhotos, Png);
        var job = factory.Services.GetRequiredService<OrphanFileCleanupJob>();

        Assert.Equal(2, await job.RunOnceAsync(DateTimeOffset.UtcNow));
        Assert.Equal(0, await job.RunOnceAsync(DateTimeOffset.UtcNow));

        Assert.Equal(aged.Except([orphanPhoto, orphanLicense]).Append(fresh).Order(), AllKeys(factory).Order());
        Assert.Equal(2, factory.StoredFiles("cooperative-logos").Count);
        Assert.Contains($"{FileCategories.RaftPhotos}/notes.txt", AllKeys(factory));
    }

    // A database that knows none of the files (wrong or empty database for this storage) deletes nothing.
    [Fact]
    public async Task A_database_pointing_to_no_file_of_a_category_deletes_nothing()
    {
        using var factory = new AuthApiFactory();
        var storage = factory.Services.GetRequiredService<IFileStorage>();
        var photo = await StoreAsync(storage, FileCategories.RaftPhotos, Png);
        var license = await StoreAsync(storage, FileCategories.ZoneLicenses, Pdf);

        Assert.Equal(0, await factory.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2)));

        Assert.Equal(new[] { photo, license }.Order(), AllKeys(factory).Order());
        Assert.Equal(2, factory.Logs.Lines.Count(l => l.StartsWith("Warning:") && l.Contains("skipped: no record points to any of its 1 old files")));
    }

    // The referenced keys are read from SQL Server; every file counts as old here, so only the reference decides.
    [SqlServerFact]
    public async Task Referenced_files_are_read_from_sql_server_and_kept()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        await (await owner.PostAsync($"/api/rafts/{raftId}/photo", new MultipartFormDataContent { { FileContent(Jpeg), "file", "raft.jpg" } }))
            .ReadAsync(HttpStatusCode.OK);
        await owner.CreateLicenseAsync(new LicenseFields { ZoneIds = [zoneId], EffectiveDate = Today });
        var referenced = host.StoredKeys(FileCategories.RaftPhotos).Concat(host.StoredKeys(FileCategories.ZoneLicenses)).Order().ToList();
        await StoreAsync(host.Api.Services.GetRequiredService<IFileStorage>(), FileCategories.ZoneLicenses, Pdf);

        Assert.Equal(1, await host.Api.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2)));

        Assert.Equal(2, referenced.Count);
        Assert.Equal(referenced, host.StoredKeys(FileCategories.RaftPhotos).Concat(host.StoredKeys(FileCategories.ZoneLicenses)).Order());
    }

    private static async Task<string> StoreAsync(IFileStorage storage, string category, byte[] content)
    {
        var validated = FileValidator.Validate([new IncomingFile("file", content.Length, new MemoryStream(content))], FileRules.Documents);
        var stored = await storage.UploadAsync(validated.Files[0], category);
        Assert.True(stored.Succeeded);
        return stored.Key!;
    }

    /// <summary>Every file under the host's storage folder, as a storage key.</summary>
    private static List<string> AllKeys(AuthApiFactory factory) => Directory.GetFiles(factory.StorageRoot, "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(factory.StorageRoot, f).Replace('\\', '/')).ToList();
}
