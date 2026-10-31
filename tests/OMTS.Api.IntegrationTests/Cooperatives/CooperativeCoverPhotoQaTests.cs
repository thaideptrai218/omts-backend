using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeCertificationRequests;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileQaKit;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>
/// QA of OMTS-217 PR 0.7, FARM-01 cover photo and the orphan file clean-up of the two new categories: failed saves
/// around the upload, a save committed before its reply was lost, files left behind by a failed removal, and both
/// categories on the Azure Blob provider.
/// </summary>
public class CooperativeCoverPhotoQaTests
{
    // A failed upload save (MSG06 or a database error) and a failed removal keep the current cover served and stored,
    // and leave no file of their own.
    [Fact]
    public async Task Failed_cover_saves_keep_the_current_cover_and_leave_no_stray_file()
    {
        using var api = new FileFaultApiFactory();
        var owner = await api.NewOwnerAsync();
        var url = (await (await owner.UploadCoverPhotoAsync(Png)).ReadAsync(HttpStatusCode.OK)).Text("coverPhotoUrl");
        var key = await CoverKeyAsync(api);

        api.Faults.FailNextSave(Conflict);
        var conflicted = await owner.UploadCoverPhotoAsync(Jpeg, "cover.jpg");
        api.Faults.FailNextSave(DatabaseDown);
        var failed = await owner.UploadCoverPhotoAsync(Jpeg, "cover.jpg");
        api.Faults.FailNextSave(DatabaseDown);
        var failedRemove = await owner.DeleteAsync(CoverPhoto);

        Assert.Equal("MSG06", await conflicted.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, failedRemove.StatusCode);
        Assert.Equal(key, await CoverKeyAsync(api));
        Assert.Equal([key!], StoredKeys(api, FileCategories.CooperativeCoverPhotos));
        Assert.Equal(Png, await api.CreateClient().GetByteArrayAsync(url));
        Assert.False(await api.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "Cooperative.CoverPhotoChanged" && e.After!.Contains(".jpg"))));
    }

    // The save committed but its reply was lost: the new cover (now the current one) is kept and served; the replaced
    // file is left to the clean-up, which removes it once past the grace period.
    [Fact]
    public async Task Cover_save_committed_before_a_lost_reply_keeps_the_new_cover_and_the_clean_up_removes_the_old()
    {
        using var api = new FileFaultApiFactory();
        var owner = await api.NewOwnerAsync();
        await (await owner.UploadCoverPhotoAsync(Png)).ReadAsync(HttpStatusCode.OK);
        var old = await CoverKeyAsync(api);

        api.Faults.FailNextSaveAfterCommit(DatabaseDown);
        var lost = await owner.UploadCoverPhotoAsync(Jpeg, "cover.jpg");
        var current = await CoverKeyAsync(api);

        Assert.Equal(HttpStatusCode.InternalServerError, lost.StatusCode);
        Assert.NotEqual(old, current);
        Assert.Equal(new[] { old!, current! }.Order(), StoredKeys(api, FileCategories.CooperativeCoverPhotos));
        var served = await api.CreateClient().GetAsync($"{CoverPhotoFiles}{current![(FileCategories.CooperativeCoverPhotos.Length + 1)..]}");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);

        Assert.Equal(1, await api.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2)));
        Assert.Equal([current], StoredKeys(api, FileCategories.CooperativeCoverPhotos));
    }

    // README: the clean-up deletes what a failed removal left behind. After the Owner removed the cover photo there is
    // no cover any more, so no stored cover file is referenced and the clean-up's "foreign database" guard skips the
    // folder for ever (with a warning on every run).
    [Fact(Skip = "QA finding OMTS-217 PR 0.7 #1 (orphan clean-up never removes a cover left behind once the cover is removed: zero references read as a foreign database)")]
    public async Task Clean_up_removes_a_cover_file_left_behind_after_the_cover_photo_was_removed()
    {
        using var api = new FileFaultApiFactory();
        var owner = await api.NewOwnerAsync();
        await (await owner.UploadCoverPhotoAsync(Png)).ReadAsync(HttpStatusCode.OK);
        api.Faults.DeleteThrows = true;
        await (await owner.DeleteAsync(CoverPhoto)).ReadAsync(HttpStatusCode.OK);
        api.Faults.DeleteThrows = false;
        Assert.Single(StoredKeys(api, FileCategories.CooperativeCoverPhotos));

        var deleted = await api.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2));

        Assert.Equal(1, deleted);
        Assert.Empty(StoredKeys(api, FileCategories.CooperativeCoverPhotos));
    }

    // The same for certificate files once the last certification is removed: the removed certificate (a business
    // document) stays in storage until a new certification is added.
    [Fact(Skip = "QA finding OMTS-217 PR 0.7 #1 (orphan clean-up never removes a certificate file left behind once no certification is left)")]
    public async Task Clean_up_removes_a_certificate_file_left_behind_after_the_last_certification_was_removed()
    {
        using var api = new FileFaultApiFactory();
        var owner = await api.NewOwnerAsync();
        var id = (await owner.CreateCertificationAsync()).Id();
        var version = await api.IssueCertificationVersionAsync(id);
        api.Faults.DeleteThrows = true;
        await (await owner.DeleteAsync($"{Certifications}/{id}?rowVersion={Uri.EscapeDataString(version)}")).ReadAsync(HttpStatusCode.NoContent);
        api.Faults.DeleteThrows = false;
        Assert.Single(StoredKeys(api, FileCategories.CooperativeCertifications));

        var deleted = await api.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2));

        Assert.Equal(1, deleted);
        Assert.Empty(StoredKeys(api, FileCategories.CooperativeCertifications));
    }

    // While one cover exists, the clean-up does remove a removal leftover and never the current cover.
    [Fact]
    public async Task Clean_up_removes_a_replaced_cover_left_behind_while_a_cover_exists_and_keeps_the_current_one()
    {
        using var api = new FileFaultApiFactory();
        var owner = await api.NewOwnerAsync();
        await (await owner.UploadCoverPhotoAsync(Png)).ReadAsync(HttpStatusCode.OK);
        api.Faults.DeleteThrows = true;
        await (await owner.UploadCoverPhotoAsync(Jpeg, "cover.jpg")).ReadAsync(HttpStatusCode.OK);
        api.Faults.DeleteThrows = false;
        var current = await CoverKeyAsync(api);
        Assert.Equal(2, StoredKeys(api, FileCategories.CooperativeCoverPhotos).Count);
        Assert.Contains(api.Logs.Lines, l => l.StartsWith("Warning:") && l.Contains("could not be removed after it was replaced"));

        var job = api.Services.GetRequiredService<OrphanFileCleanupJob>();
        Assert.Equal(0, await job.RunOnceAsync(DateTimeOffset.UtcNow));
        Assert.Equal(1, await job.RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2)));
        Assert.Equal([current!], StoredKeys(api, FileCategories.CooperativeCoverPhotos));
        Assert.Equal(current, await CoverKeyAsync(api));
        Assert.Equal(2, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "Cooperative.CoverPhotoChanged" && e.RecordType == nameof(Cooperative))));
    }

    // The production provider (Azure Blob, against Azurite): the public route serves the current cover only and a
    // replaced cover blob is deleted; the certificate's SAS link reads the blob until the certification is removed;
    // the clean-up keeps the current cover.
    [AzuriteFact]
    public async Task Cover_photo_and_certificate_files_work_against_blob_storage()
    {
        using var api = new BlobApiFactory(Environment.GetEnvironmentVariable(AzuriteFactAttribute.VariableName)!);
        var owner = await api.NewOwnerAsync();
        var storage = api.Services.GetRequiredService<IFileStorage>();
        var anonymous = api.CreateClient();

        var first = (await (await owner.UploadCoverPhotoAsync(Png)).ReadAsync(HttpStatusCode.OK)).Text("coverPhotoUrl");
        var firstKey = await CoverKeyAsync(api);
        Assert.Equal(Png, await anonymous.GetByteArrayAsync(first));
        var second = (await (await owner.UploadCoverPhotoAsync(Jpeg, "cover.jpg")).ReadAsync(HttpStatusCode.OK)).Text("coverPhotoUrl");
        var served = await anonymous.GetAsync(second);

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(first)).StatusCode);
        Assert.Null(await storage.OpenReadAsync(firstKey!));
        Assert.Equal("image/jpeg", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Jpeg, await served.Content.ReadAsByteArrayAsync());

        var certification = await owner.CreateCertificationAsync();
        var link = certification.Text("fileUrl");
        using var http = new HttpClient();
        Assert.Equal(Pdf, await http.GetByteArrayAsync(link));
        var version = await api.IssueCertificationVersionAsync(certification.Id());
        await (await owner.DeleteAsync($"{Certifications}/{certification.Id()}?rowVersion={Uri.EscapeDataString(version)}")).ReadAsync(HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(link)).StatusCode);

        Assert.Equal(0, await api.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2)));
        Assert.Equal(Jpeg, await anonymous.GetByteArrayAsync(second));
    }

    /// <summary>The API with <c>Storage:Provider = AzureBlob</c> on a container of its own.</summary>
    private sealed class BlobApiFactory(string connectionString) : AuthApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Storage:Provider", "AzureBlob");
            builder.UseSetting("Storage:AzureBlob:ConnectionString", connectionString);
            builder.UseSetting("Storage:AzureBlob:Container", $"omts-test-{Guid.NewGuid():N}");
        }
    }
}
