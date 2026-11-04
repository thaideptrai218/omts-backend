using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Farming.FarmingUploadedFileTests;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>Independent QA of FARM-04 photos: slots, failures, links, the Discontinued raft and the orphan file job.</summary>
public class RaftsV2QaTestsPhotos
{
    private static List<int> Slots(JsonElement raft) => raft.GetProperty("photos").EnumerateArray().Select(p => p.GetProperty("sortOrder").GetInt32()).ToList();

    private static void AgeAll(AuthApiFactory factory)
    {
        foreach (var file in Directory.GetFiles(factory.StorageRoot, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3));
        }
    }

    [Fact]
    public async Task The_lowest_free_slot_is_reused_and_each_photo_keeps_its_id_and_slot()
    {
        using var factory = new AuthApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        var raftId = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id();
        await owner.AddRaftPhotoAsync(raftId);
        await owner.AddRaftPhotoAsync(raftId);
        var three = await owner.AddRaftPhotoAsync(raftId);
        var ids = three.GetProperty("photos").EnumerateArray().Select(p => p.Id()).ToList();

        await (await owner.DeleteAsync($"/api/rafts/{raftId}/photos/{ids[0]}")).ReadAsync(HttpStatusCode.OK);
        var left = await (await owner.DeleteAsync($"/api/rafts/{raftId}/photos/{ids[2]}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal([2], Slots(left.GetProperty("raft")));

        var first = await owner.AddRaftPhotoAsync(raftId);
        Assert.Equal([1, 2], Slots(first));
        var last = await owner.AddRaftPhotoAsync(raftId);
        Assert.Equal([1, 2, 3], Slots(last));
        Assert.Equal(ids[1], last.GetProperty("photos")[1].Id());
        Assert.Equal(3, factory.StoredFiles("raft-photos").Count);
    }

    // GFR-01: the file store refuses: MSG-GEN-03, no row, no audit, and the raft may try again.
    [Fact]
    public async Task A_save_that_fails_after_the_upload_keeps_no_row_and_no_file_and_the_next_try_works()
    {
        using var factory = new FileFaultApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        var raftId = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id();
        await owner.AddRaftPhotoAsync(raftId);
        var first = Assert.Single(await factory.RaftPhotoKeysAsync(raftId));

        // Lost to a parallel upload: the same answer as a lost row version, and the upload is removed.
        factory.Faults.FailNextSave(new DuplicateRecordException(new InvalidOperationException("Test: slot taken.")));
        Assert.Equal("MSG06", await (await owner.UploadRaftPhotoAsync(raftId)).ErrorCodeAsync());
        Assert.Equal([first], await factory.RaftPhotoKeysAsync(raftId));
        Assert.Equal([first], StoredKeys(factory, FileCategories.RaftPhotos));

        Assert.Equal([1, 2], Slots(await owner.AddRaftPhotoAsync(raftId)));
    }

    // The signature is the permission: the link works without a login, not when tampered with, and not once the photo is removed.
    [Fact]
    public async Task A_photo_link_opens_for_anyone_holding_it_until_the_photo_is_removed()
    {
        using var factory = new AuthApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        var raftId = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id();
        var photo = (await owner.AddRaftPhotoAsync(raftId)).GetProperty("photos")[0];
        var url = photo.Text("url");
        var anonymous = factory.CreateClient();

        var read = await anonymous.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("image/png", read.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(url + "x")).StatusCode);

        await (await owner.DeleteAsync($"/api/rafts/{raftId}/photos/{photo.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(url)).StatusCode);
    }

    // The whole FARM-04 file rule on the raft that holds the photos, and every refusal stores nothing.
    [Fact]
    public async Task A_refused_upload_stores_nothing_in_any_state_of_the_raft()
    {
        using var factory = new AuthApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await owner.CreateZoneAsync();
        var raft = await owner.CreateRaftAsync(zone.Id());

        Assert.Equal(["Photo"], await (await owner.UploadRaftPhotoAsync(raft.Id(), [], "empty.png")).InvalidFieldsAsync());
        Assert.Empty(factory.StoredFiles("raft-photos"));

        // Unknown raft and an unknown photo of a Discontinued raft are 404.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.UploadRaftPhotoAsync(Guid.NewGuid())).StatusCode);

        var current = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}");
        await (await owner.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = current.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/rafts/{raft.Id()}/photos/{Guid.NewGuid()}")).StatusCode);

        // The status comes before the file.
        Assert.Equal("MSG-GEN-09", await (await owner.UploadRaftPhotoAsync(raft.Id(), TextPretendingToBeAFile)).ErrorCodeAsync());
        Assert.Empty(factory.StoredFiles("raft-photos"));
    }

    // FINDING L-3. "One photo per request" (client list), and the service sets MaxFiles = 1, but the action binds a single IFormFile, so that rule
    // never fires: a form with two files answers 200 "Raft photo saved." and the second file is dropped without a word (GFR-01, MSG-GEN-01).
    [Fact]
    public async Task A_form_with_two_files_does_not_silently_drop_one()
    {
        using var factory = new AuthApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        var raftId = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id();

        var answer = await owner.PostAsync($"/api/rafts/{raftId}/photos", new MultipartFormDataContent
        {
            { FileContent(Png, "image/png"), "file", "one.png" },
            { FileContent(Jpeg, "image/jpeg"), "file", "two.jpg" },
        });

        var stored = (await factory.RaftPhotoKeysAsync(raftId)).Count;
        Assert.True(answer.StatusCode == HttpStatusCode.BadRequest || stored == 2, $"{(int)answer.StatusCode}, {stored} photo(s) stored of the 2 sent");
    }

    // A raft In Use in a Discontinued zone (older database): photo writes are refused naming the zone, reads stay open.
    [Fact]
    public async Task Photo_writes_of_a_raft_in_a_discontinued_zone_are_refused_naming_the_zone()
    {
        using var factory = new AuthApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await owner.CreateZoneAsync();
        var raftId = (await owner.CreateRaftAsync(zone.Id())).Id();
        var photo = (await owner.AddRaftPhotoAsync(raftId)).GetProperty("photos")[0];
        await factory.UpdateAsync<FarmingZone>(zone.Id(), z => z.StatusId = StatusIds.Usage.Discontinued);

        var upload = await (await owner.UploadRaftPhotoAsync(raftId)).ReadAsync(HttpStatusCode.Conflict);
        var remove = await (await owner.DeleteAsync($"/api/rafts/{raftId}/photos/{photo.Id()}")).ReadAsync(HttpStatusCode.Conflict);
        foreach (var refused in new[] { upload, remove })
        {
            Assert.Equal(("MSG-GEN-09", $"Farming zone {zone.Text("code")} is Discontinued and cannot be used for this action."), (refused.Text("code"), refused.Text("detail")));
        }

        Assert.Equal(1, (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftId}")).GetProperty("photos").GetArrayLength());
        Assert.Single(factory.StoredFiles("raft-photos"));
    }

    // The orphan job keeps a photo in any slot, of an In Use or a Discontinued raft, and removes one whose delete failed.
    [Fact]
    public async Task The_orphan_job_keeps_every_slot_and_removes_a_photo_removed_earlier()
    {
        using var factory = new FileFaultApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raft = await owner.CreateRaftAsync(zoneId);
        await owner.AddRaftPhotoAsync(raft.Id());
        await owner.AddRaftPhotoAsync(raft.Id());
        var three = await owner.AddRaftPhotoAsync(raft.Id());
        var other = await owner.CreateRaftAsync(zoneId);
        await owner.AddRaftPhotoAsync(other.Id());
        var stopped = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{other.Id()}");
        await (await owner.PostAsJsonAsync($"/api/rafts/{other.Id()}/discontinue", new { rowVersion = stopped.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);

        var removedKey = (await factory.RaftPhotoKeysAsync(raft.Id()))[1];
        factory.Faults.DeleteThrows = true;
        await (await owner.DeleteAsync($"/api/rafts/{raft.Id()}/photos/{three.GetProperty("photos")[1].Id()}")).ReadAsync(HttpStatusCode.OK);
        factory.Faults.DeleteThrows = false;
        Assert.Contains(removedKey, StoredKeys(factory, FileCategories.RaftPhotos));
        AgeAll(factory);

        Assert.Equal(1, await factory.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow));

        var kept = (await factory.RaftPhotoKeysAsync(raft.Id())).Concat(await factory.RaftPhotoKeysAsync(other.Id())).Order().ToList();
        Assert.Equal(3, kept.Count);
        Assert.Equal(kept, StoredKeys(factory, FileCategories.RaftPhotos).Order());
    }

    // FINDING L-1. The job guards against a foreign database with "no stored raft photo is referenced". Since this PR a real database may
    // reach that state: the only photo is removed and its delete fails. The orphan then stays until another raft photo is saved. Cooperative
    // covers and certificates are exempt from the guard for exactly this reason (OrphanFileCleanupService.CooperativeCategories).
    [Fact]
    public async Task The_orphan_job_removes_the_only_raft_photo_after_its_removal_failed()
    {
        using var factory = new FileFaultApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        var raftId = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id();
        var photo = (await owner.AddRaftPhotoAsync(raftId)).GetProperty("photos")[0].Id();
        factory.Faults.DeleteThrows = true;
        await (await owner.DeleteAsync($"/api/rafts/{raftId}/photos/{photo}")).ReadAsync(HttpStatusCode.OK);
        factory.Faults.DeleteThrows = false;
        AgeAll(factory);

        await factory.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow);

        Assert.Empty(StoredKeys(factory, FileCategories.RaftPhotos));
    }
}
