using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Files;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Farming.FarmingUploadedFileTests;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>FARM-04 raft photos: at most three, JPEG or PNG of at most 10 MB, added and removed one at a time, audited.</summary>
public class RaftPhotoTests
{
    private static readonly int[] SlotsOfThree = [1, 2, 3];

    [Fact]
    public async Task Photo_must_really_be_jpeg_or_png_of_at_most_10_MB()
    {
        using var factory = new AuthApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id());
        var url = $"/api/rafts/{raft.Id()}/photos";

        Assert.Equal(["Photo"], await (await owner.UploadRaftPhotoAsync(raft.Id(), TextPretendingToBeAFile)).InvalidFieldsAsync());
        Assert.Equal(["Photo"], await (await owner.UploadRaftPhotoAsync(raft.Id(), Pdf, "raft.jpg")).InvalidFieldsAsync());
        Assert.Equal(["Photo"], await (await owner.UploadRaftPhotoAsync(raft.Id(), WebP, "raft.webp")).InvalidFieldsAsync());
        Assert.Equal(["Photo"], await (await owner.UploadRaftPhotoAsync(raft.Id(), Sized(Png, 10 * 1024 * 1024 + 1), "big.png")).InvalidFieldsAsync());
        Assert.Equal(["Photo"], await (await owner.PostAsync(url, new MultipartFormDataContent { { new StringContent("x"), "note" } })).InvalidFieldsAsync());
        Assert.Empty(factory.StoredFiles("raft-photos"));
        Assert.Empty(await factory.RaftPhotoKeysAsync(raft.Id()));

        var saved = await (await owner.UploadRaftPhotoAsync(raft.Id(), Sized(Png, 10 * 1024 * 1024), "exactly-10-mb.png")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("Raft photo saved.", "MSG-GEN-05"), (saved.Text("message"), saved.Text("messageCode")));
        Assert.NotNull(saved.GetProperty("raft").GetProperty("photos")[0].Text("url"));
        Assert.Equal(2, (await owner.AddRaftPhotoAsync(raft.Id(), Jpeg)).GetProperty("photos").GetArrayLength());
        Assert.Equal(2, factory.StoredFiles("raft-photos").Count);
    }

    [Fact]
    public async Task A_fourth_photo_is_refused_without_storing_a_file_and_a_removed_slot_is_used_again()
    {
        using var factory = new AuthApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        var raftId = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id();
        await owner.AddRaftPhotoAsync(raftId);
        await owner.AddRaftPhotoAsync(raftId);
        var three = await owner.AddRaftPhotoAsync(raftId);
        Assert.Equal(SlotsOfThree, Slots(three));

        var fourth = await (await owner.UploadRaftPhotoAsync(raftId)).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Photos"), (fourth.Text("code"), fourth.Text("field")));
        Assert.Contains("at most 3", fourth.Text("hint"));
        Assert.Equal(3, factory.StoredFiles("raft-photos").Count);

        // Removing the middle one frees its slot and its file; the next photo takes that slot.
        var middle = three.GetProperty("photos")[1].Id();
        var removedKey = (await factory.RaftPhotoKeysAsync(raftId))[1];
        var removed = await (await owner.DeleteAsync($"/api/rafts/{raftId}/photos/{middle}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("Raft saved.", "MSG-GEN-05"), (removed.Text("message"), removed.Text("messageCode")));
        Assert.Equal([1, 3], Slots(removed.GetProperty("raft")));
        Assert.DoesNotContain(removedKey, StoredKeys(factory, FileCategories.RaftPhotos));

        Assert.Equal(SlotsOfThree, Slots(await owner.AddRaftPhotoAsync(raftId)));
        Assert.Equal(3, factory.StoredFiles("raft-photos").Count);

        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/rafts/{raftId}/photos/{middle}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/rafts/{Guid.NewGuid()}/photos/{middle}")).StatusCode);
    }

    [Fact]
    public async Task A_photo_of_another_raft_is_not_removed_through_this_raft()
    {
        using var factory = new AuthApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var first = (await owner.CreateRaftAsync(zoneId)).Id();
        var second = (await owner.CreateRaftAsync(zoneId)).Id();
        var photo = (await owner.AddRaftPhotoAsync(second)).GetProperty("photos")[0].Id();

        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/rafts/{first}/photos/{photo}")).StatusCode);
        Assert.Single(await factory.RaftPhotoKeysAsync(second));
    }

    // GBR-02: both directions keep the file keys before and after.
    [Fact]
    public async Task Adding_and_removing_a_photo_are_audited_with_before_and_after()
    {
        using var factory = new AuthApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        var raftId = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id();

        var added = await owner.AddRaftPhotoAsync(raftId);
        var key = Assert.Single(await factory.RaftPhotoKeysAsync(raftId));
        await (await owner.DeleteAsync($"/api/rafts/{raftId}/photos/{added.GetProperty("photos")[0].Id()}")).ReadAsync(HttpStatusCode.OK);

        var audits = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == raftId && e.Action == "Raft.PhotosChanged").OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(2, audits.Count);
        Assert.Empty(Keys(audits[0].Before!));
        Assert.Equal([key], Keys(audits[0].After!));
        Assert.Equal([key], Keys(audits[1].Before!));
        Assert.Empty(Keys(audits[1].After!));

        static IEnumerable<string> Keys(string json) => JsonNode.Parse(json)!["Photos"]!.AsArray().Select(k => k!.GetValue<string>());
    }

    [Fact]
    public async Task Removing_a_photo_whose_file_cannot_be_deleted_still_answers_and_leaves_the_file_to_the_clean_up()
    {
        using var factory = new FileFaultApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        var raftId = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id();
        var photo = (await owner.AddRaftPhotoAsync(raftId)).GetProperty("photos")[0].Id();
        factory.Faults.DeleteThrows = true;

        await (await owner.DeleteAsync($"/api/rafts/{raftId}/photos/{photo}")).ReadAsync(HttpStatusCode.OK);

        Assert.Empty(await factory.RaftPhotoKeysAsync(raftId));
        Assert.Single(StoredKeys(factory, FileCategories.RaftPhotos));
        Assert.Contains(factory.Logs.Lines, l => l.StartsWith("Warning:") && l.Contains("could not be removed after it was replaced"));
    }

    private static IEnumerable<int> Slots(JsonElement raft) => raft.GetProperty("photos").EnumerateArray().Select(p => p.GetProperty("sortOrder").GetInt32());
}
