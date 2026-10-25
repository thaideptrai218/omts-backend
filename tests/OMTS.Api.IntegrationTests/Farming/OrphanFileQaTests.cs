using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Farming;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// QA of the OMTS-214 orphan file removal: the "no file of a folder referenced" guard, the categories that are never
/// scanned, the grace period, the option check, blob storage and parallel saves on SQL Server.
/// </summary>
public class OrphanFileQaTests
{
    // README: "A run that finds no file of a folder referenced at all deletes nothing". Records of another
    // environment's database (e.g. staging run against the production container) point to keys this storage never
    // held: none of the stored files is referenced, so nothing may be deleted.
    [Fact]
    public async Task A_database_whose_records_point_to_none_of_the_stored_files_deletes_nothing()
    {
        using var factory = new AuthApiFactory();
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var raftId = (await owner.Client.CreateRaftAsync(zoneId)).Id();
        var licenseId = (await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneIds = [zoneId], EffectiveDate = Today })).Id();
        await factory.UpdateAsync<Raft>(raftId, r => r.PhotoUrl = ForeignKey(FileCategories.RaftPhotos, "jpg"));
        await factory.UpdateAsync<ZoneComplianceDocument>(licenseId, d => d.EvidenceFileUrl = ForeignKey(FileCategories.ZoneLicenses, "pdf"));
        var storage = factory.Services.GetRequiredService<IFileStorage>();
        await StoreAsync(storage, FileCategories.RaftPhotos, Png);
        await StoreAsync(storage, FileCategories.RaftPhotos, Jpeg);
        var stored = AllKeys(factory);
        AgeAll(factory, TimeSpan.FromDays(2));

        var deleted = await factory.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow);

        Assert.Equal(stored.Order(), AllKeys(factory).Order());
        Assert.Equal(0, deleted);
    }

    // Only raft-photos/ and zone-licenses/ are scanned; similar names are other categories.
    [Fact]
    public async Task Old_unreferenced_files_of_every_other_category_are_never_touched()
    {
        using var factory = new AuthApiFactory();
        var owner = await factory.OwnerAsync();
        var raftId = (await owner.Client.CreateRaftAsync((await owner.Client.CreateZoneAsync()).Id())).Id();
        await (await UploadPhotoAsync(owner.Client, raftId)).ReadAsync(HttpStatusCode.OK);
        var storage = factory.Services.GetRequiredService<IFileStorage>();
        var orphan = await StoreAsync(storage, FileCategories.RaftPhotos, Png);
        var others = new List<string>();
        foreach (var category in new[]
                 {
                     "cooperative-logos", "water-meter-photos", "supplier-certificates", "purchase-invoices", "raft-photos-old", "zone-licenses-archive",
                 })
        {
            others.Add(await StoreAsync(storage, category, Png));
        }

        AgeAll(factory, TimeSpan.FromDays(30));

        Assert.Equal(1, await factory.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow));

        var left = AllKeys(factory);
        Assert.DoesNotContain(orphan, left);
        Assert.All(others, key => Assert.Contains(key, left));
    }

    [Fact]
    public async Task A_file_written_just_inside_the_grace_period_is_kept()
    {
        using var factory = new AuthApiFactory();
        var owner = await factory.OwnerAsync();
        var raftId = (await owner.Client.CreateRaftAsync((await owner.Client.CreateZoneAsync()).Id())).Id();
        await (await UploadPhotoAsync(owner.Client, raftId)).ReadAsync(HttpStatusCode.OK);
        var storage = factory.Services.GetRequiredService<IFileStorage>();
        var inside = await StoreAsync(storage, FileCategories.RaftPhotos, Png);
        var outside = await StoreAsync(storage, FileCategories.RaftPhotos, Png);
        var now = DateTimeOffset.UtcNow;
        AgeAll(factory, TimeSpan.FromDays(3));
        File.SetLastWriteTimeUtc(Path.Combine(factory.StorageRoot, inside), (now - TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1)).UtcDateTime);
        File.SetLastWriteTimeUtc(Path.Combine(factory.StorageRoot, outside), (now - TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1)).UtcDateTime);

        Assert.Equal(1, await factory.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(now));

        var left = AllKeys(factory);
        Assert.Contains(inside, left);
        Assert.DoesNotContain(outside, left);
        Assert.Contains(await factory.QueryAsync(db => db.Rafts.Where(r => r.Id == raftId).Select(r => r.PhotoUrl!).SingleAsync()), left);
    }

    [Fact]
    public void A_grace_period_under_one_hour_stops_the_host()
    {
        using var factory = new AuthApiFactory();
        using var shortGrace = factory.WithWebHostBuilder(b => b.UseSetting("Jobs:OrphanFileCleanup:GracePeriod", "00:59:00"));

        var error = Assert.ThrowsAny<Exception>(() => shortGrace.CreateClient());

        Assert.Contains("GracePeriod must be at least 01:00:00", error.ToString());
    }

    // The Azure provider end to end: the replaced blobs are deleted after the save, the clean-up deletes the orphan
    // blob and keeps the blobs the raft and the license point to.
    [AzuriteFact]
    public async Task On_blob_storage_replaced_files_are_removed_and_the_clean_up_keeps_the_referenced_blobs()
    {
        using var factory = new BlobStorageApiFactory();
        var owner = await factory.OwnerAsync();
        var storage = factory.Services.GetRequiredService<IFileStorage>();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var raftId = (await owner.Client.CreateRaftAsync(zoneId)).Id();
        await (await UploadPhotoAsync(owner.Client, raftId)).ReadAsync(HttpStatusCode.OK);
        await (await UploadPhotoAsync(owner.Client, raftId)).ReadAsync(HttpStatusCode.OK);
        var photo = await factory.QueryAsync(db => db.Rafts.Where(r => r.Id == raftId).Select(r => r.PhotoUrl!).SingleAsync());
        Assert.Equal([photo], await KeysAsync(storage, FileCategories.RaftPhotos));

        var fields = new LicenseFields { ZoneIds = [zoneId], EffectiveDate = Today };
        var license = await owner.Client.CreateLicenseAsync(fields);
        var edit = fields with { Holder = "HTX", RowVersion = await factory.IssueRowVersionAsync<ZoneComplianceDocument>(license.Id()) };
        await (await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", edit.ToForm())).ReadAsync(HttpStatusCode.OK);
        var licenseFile = await factory.QueryAsync(db => db.ZoneComplianceDocuments.Where(d => d.Id == license.Id()).Select(d => d.EvidenceFileUrl!).SingleAsync());
        Assert.Equal([licenseFile], await KeysAsync(storage, FileCategories.ZoneLicenses));

        var orphan = await StoreAsync(storage, FileCategories.RaftPhotos, Png);
        Assert.Contains(orphan, await KeysAsync(storage, FileCategories.RaftPhotos));
        // Blob write times come from the emulator: run as if two days later.
        Assert.Equal(1, await factory.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2)));

        Assert.Equal([photo], await KeysAsync(storage, FileCategories.RaftPhotos));
        Assert.Equal([licenseFile], await KeysAsync(storage, FileCategories.ZoneLicenses));
    }

    // Parallel saves of one raft photo or one license: whatever answer each request gets, the file the saved record
    // points to is never deleted, and the clean-up then leaves exactly the referenced files.
    [SqlServerFact]
    public async Task Parallel_photo_uploads_and_license_edits_never_delete_the_file_the_saved_record_points_to()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        await (await UploadPhotoAsync(owner, raftId)).ReadAsync(HttpStatusCode.OK);
        string? photo = null;
        for (var round = 0; round < 3; round++)
        {
            var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => UploadPhotoAsync(owner, raftId)));
            foreach (var answer in answers)
            {
                Assert.True(answer.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"round {round}: {(int)answer.StatusCode} {await answer.Content.ReadAsStringAsync()}");
            }

            Assert.Contains(answers, a => a.StatusCode == HttpStatusCode.OK);
            photo = await host.QueryAsync(db => db.Rafts.Where(r => r.Id == raftId).Select(r => r.PhotoUrl).SingleAsync());
            Assert.Contains(photo, host.StoredKeys(FileCategories.RaftPhotos));
        }

        var fields = new LicenseFields { ZoneIds = [zoneId], EffectiveDate = Today };
        var license = await owner.CreateLicenseAsync(fields);
        var edit = fields with { Holder = "HTX Trung Nam", RowVersion = license.Text("rowVersion") };
        var edits = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => owner.PutAsync($"/api/zone-licenses/{license.Id()}", edit.ToForm())));
        Assert.Single(edits, e => e.StatusCode == HttpStatusCode.OK);
        foreach (var refused in edits.Where(e => e.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal("MSG06", await refused.ErrorCodeAsync());
        }

        var licenseFile = await host.QueryAsync(db => db.ZoneComplianceDocuments.Where(d => d.Id == license.Id()).Select(d => d.EvidenceFileUrl).SingleAsync());
        Assert.Contains(licenseFile, host.StoredKeys(FileCategories.ZoneLicenses));

        await host.Api.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2));

        Assert.Equal([photo!], host.StoredKeys(FileCategories.RaftPhotos));
        Assert.Equal([licenseFile!], host.StoredKeys(FileCategories.ZoneLicenses));
    }

    /// <summary>A storage key of the right shape that this storage never held.</summary>
    private static string ForeignKey(string category, string extension) => $"{category}/2020/01/{Guid.NewGuid():N}.{extension}";

    private static Task<HttpResponseMessage> UploadPhotoAsync(HttpClient owner, Guid raftId) =>
        owner.PostAsync($"/api/rafts/{raftId}/photo", new MultipartFormDataContent { { FileContent(Png), "file", "raft.png" } });

    private static async Task<string> StoreAsync(IFileStorage storage, string category, byte[] content)
    {
        var validated = FileValidator.Validate([new IncomingFile("file", content.Length, new MemoryStream(content))], FileRules.Documents);
        var stored = await storage.UploadAsync(validated.Files[0], category);
        Assert.True(stored.Succeeded);
        return stored.Key!;
    }

    private static async Task<List<string>> KeysAsync(IFileStorage storage, string category) =>
        (await storage.ListAsync(category)).Select(f => f.Key).Order().ToList();

    /// <summary>Every file under the host's Local storage folder, as a storage key.</summary>
    private static List<string> AllKeys(AuthApiFactory factory) => Directory.GetFiles(factory.StorageRoot, "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(factory.StorageRoot, f).Replace('\\', '/')).ToList();

    private static void AgeAll(AuthApiFactory factory, TimeSpan age)
    {
        foreach (var file in Directory.GetFiles(factory.StorageRoot, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow - age);
        }
    }

    /// <summary>The test host on Azurite (<see cref="AzuriteFactAttribute"/>), with a container of its own.</summary>
    private sealed class BlobStorageApiFactory : AuthApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Storage:Provider", "AzureBlob");
            builder.UseSetting("Storage:AzureBlob:ConnectionString", Environment.GetEnvironmentVariable(AzuriteFactAttribute.VariableName));
            builder.UseSetting("Storage:AzureBlob:Container", $"omts-test-{Guid.NewGuid():N}");
        }
    }
}
