using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Farming;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// FARM-03/04 stored files (OMTS-214): a failed save removes its upload, a replaced file is removed only after the
/// save, and a failing removal is logged without hiding the save's answer.
/// </summary>
public class FarmingUploadedFileTests
{
    private static ConcurrencyConflictException Conflict => new(new InvalidOperationException("Test: changed by another request."));

    private static InvalidOperationException DatabaseDown => new("Test: the database is down.");

    [Fact]
    public async Task Raft_photo_of_a_failed_save_is_removed_and_the_replaced_photo_only_after_the_save()
    {
        using var factory = new FileFaultApiFactory();
        var owner = await factory.OwnerAsync();
        var raftId = (await owner.Client.CreateRaftAsync((await owner.Client.CreateZoneAsync()).Id())).Id();
        await (await UploadPhotoAsync(owner, raftId)).ReadAsync(HttpStatusCode.OK);
        var first = await PhotoKeyAsync(factory, raftId);

        factory.Faults.FailNextSave(Conflict);
        Assert.Equal("MSG06", await (await UploadPhotoAsync(owner, raftId)).ErrorCodeAsync());
        factory.Faults.FailNextSave(DatabaseDown);
        Assert.Equal(HttpStatusCode.InternalServerError, (await UploadPhotoAsync(owner, raftId)).StatusCode);

        // Both failed saves left the current photo and removed their own upload.
        Assert.Equal(first, await PhotoKeyAsync(factory, raftId));
        Assert.Equal([first], StoredKeys(factory, FileCategories.RaftPhotos));

        await (await UploadPhotoAsync(owner, raftId)).ReadAsync(HttpStatusCode.OK);
        var second = await PhotoKeyAsync(factory, raftId);
        Assert.NotEqual(first, second);
        Assert.Equal([second], StoredKeys(factory, FileCategories.RaftPhotos));
    }

    [Fact]
    public async Task License_file_replaced_by_an_edit_is_removed_after_the_save_and_a_renewal_keeps_the_old_file()
    {
        using var factory = new FileFaultApiFactory();
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var fields = new LicenseFields { ZoneIds = [zoneId], EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddYears(1) };
        var license = await owner.Client.CreateLicenseAsync(fields);
        var original = await LicenseFileAsync(factory, license.Id());
        var edit = fields with { Holder = "HTX Trung Nam", RowVersion = await factory.IssueRowVersionAsync<ZoneComplianceDocument>(license.Id()) };

        factory.Faults.FailNextSave(Conflict);
        Assert.Equal("MSG06", await (await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", edit.ToForm())).ErrorCodeAsync());
        Assert.Equal(original, await LicenseFileAsync(factory, license.Id()));
        Assert.Equal([original], StoredKeys(factory, FileCategories.ZoneLicenses));

        var edited = await (await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", edit.ToForm())).ReadAsync(HttpStatusCode.OK);
        var corrected = await LicenseFileAsync(factory, license.Id());
        Assert.NotEqual(original, corrected);
        Assert.Equal([corrected], StoredKeys(factory, FileCategories.ZoneLicenses));

        // An edit without a file keeps the file; a renewal adds its own and the replaced license keeps its file.
        await (await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", (edit with { Holder = "HTX", File = null, RowVersion = edited.Text("rowVersion") }).ToForm()))
            .ReadAsync(HttpStatusCode.OK);
        var renewal = await (await owner.Client.PostAsync($"/api/zone-licenses/{license.Id()}/replace", new LicenseFields { EffectiveDate = Today }.ToForm()))
            .ReadAsync(HttpStatusCode.Created);
        var kept = new[] { corrected, await LicenseFileAsync(factory, renewal.Id()) }.Order().ToList();
        Assert.Equal(kept, StoredKeys(factory, FileCategories.ZoneLicenses));

        factory.Faults.FailNextSave(DatabaseDown);
        var failedCreate = await owner.Client.PostAsync("/api/zone-licenses", new LicenseFields { ZoneIds = [zoneId], EffectiveDate = Today }.ToForm());
        Assert.Equal(HttpStatusCode.InternalServerError, failedCreate.StatusCode);
        Assert.Equal(kept, StoredKeys(factory, FileCategories.ZoneLicenses));
    }

    [Fact]
    public async Task A_failing_removal_is_logged_never_hides_the_answer_and_is_left_to_the_clean_up()
    {
        using var factory = new FileFaultApiFactory();
        var owner = await factory.OwnerAsync();
        var raftId = (await owner.Client.CreateRaftAsync((await owner.Client.CreateZoneAsync()).Id())).Id();
        await (await UploadPhotoAsync(owner, raftId)).ReadAsync(HttpStatusCode.OK);
        factory.Faults.DeleteThrows = true;

        factory.Faults.FailNextSave(Conflict);
        Assert.Equal("MSG06", await (await UploadPhotoAsync(owner, raftId)).ErrorCodeAsync());
        await (await UploadPhotoAsync(owner, raftId)).ReadAsync(HttpStatusCode.OK);

        var current = await PhotoKeyAsync(factory, raftId);
        Assert.Equal(3, StoredKeys(factory, FileCategories.RaftPhotos).Count);
        Assert.Contains(factory.Logs.Lines, l => l.StartsWith("Warning:") && l.Contains("could not be removed after a failed save"));
        Assert.Contains(factory.Logs.Lines, l => l.StartsWith("Warning:") && l.Contains("could not be removed after it was replaced"));

        // Once past the grace period, the clean-up removes both leftovers and keeps the current photo.
        factory.Faults.DeleteThrows = false;
        var job = factory.Services.GetRequiredService<OrphanFileCleanupJob>();
        Assert.Equal(0, await job.RunOnceAsync(DateTimeOffset.UtcNow));
        Assert.Equal(2, await job.RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2)));
        Assert.Equal([current], StoredKeys(factory, FileCategories.RaftPhotos));
    }

    private static Task<HttpResponseMessage> UploadPhotoAsync(Actor owner, Guid raftId) =>
        owner.Client.PostAsync($"/api/rafts/{raftId}/photo", new MultipartFormDataContent { { FileContent(Png), "file", "raft.png" } });

    private static Task<string?> PhotoKeyAsync(AuthApiFactory factory, Guid raftId) =>
        factory.QueryAsync(db => db.Rafts.Where(r => r.Id == raftId).Select(r => r.PhotoUrl).SingleAsync());

    private static Task<string?> LicenseFileAsync(AuthApiFactory factory, Guid licenseId) =>
        factory.QueryAsync(db => db.ZoneComplianceDocuments.Where(d => d.Id == licenseId).Select(d => d.EvidenceFileUrl).SingleAsync());

    /// <summary>Storage keys of the Local files kept under a category, in order.</summary>
    internal static List<string?> StoredKeys(AuthApiFactory factory, string category) =>
        factory.StoredFiles(category).Select(f => (string?)Path.GetRelativePath(factory.StorageRoot, f).Replace('\\', '/')).Order().ToList();
}
