using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Farming.FarmingUploadedFileTests;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>Photos on a correction and a removal: kept and removed, GFR-01 when one cannot be uploaded, and the minutes of an upload at sea.</summary>
public class HarvestLotCorrectionPhotoTests
{
    private const string Category = FileCategories.HarvestLotPhotos;

    private static ConcurrencyConflictException Conflict => new(new InvalidOperationException("Test: changed by another request."));

    [Fact]
    public async Task Kept_photos_stay_in_their_slots_new_ones_take_the_free_slots_and_a_photo_not_kept_leaves_the_store()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg, Png, Jpeg] });
        var keys = lot.PhotoKeysOfLot();

        var saved = (await api.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Photos = [Png] }, keep: [keys[0], keys[2]])).GetProperty("lot");

        var photos = saved.GetProperty("photos").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3], photos.Select(p => p.GetProperty("sortOrder").GetInt32()));
        Assert.Equal(keys[0], photos[0].Text("key"));
        Assert.Equal(keys[2], photos[2].Text("key"));
        Assert.DoesNotContain(keys[1], photos.Select(p => p.Text("key")));
        Assert.Equal(photos.Select(p => p.Text("key")).Order(), StoredKeys(api, Category)!);
        Assert.Contains(keys[1], await api.QueryAsync(db => db.AuditEvents.Where(e => e.Action == "HarvestLot.Corrected").Select(e => e.Before!).SingleAsync()));
    }

    // Photos are optional on a lot: no kept key and no new photo leaves none, which a log refuses.
    [Fact]
    public async Task Sending_no_kept_key_removes_every_photo_and_a_lot_may_end_with_none()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });

        var saved = (await api.CorrectLotAsync(owner, lot.Id(), new HarvestFields())).GetProperty("lot");

        Assert.Empty(saved.PhotoKeysOfLot());
        Assert.Empty(StoredKeys(api, Category));
    }

    [Fact]
    public async Task An_unknown_key_too_many_photos_a_wrong_format_or_another_part_is_refused_and_stores_nothing()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var other = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["2"], Photos = [Png] });
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });
        var keys = lot.PhotoKeysOfLot();
        var stored = StoredKeys(api, Category).ToList();

        var unknown = await (await api.SendCorrectionAsync(owner, lot.Id(), new HarvestFields(), keep: other.PhotoKeysOfLot())).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Photos", "Keep only photos of this lot."), (unknown.Text("field"), unknown.Text("hint")));
        var spelled = await (await api.SendCorrectionAsync(owner, lot.Id(), new HarvestFields(), keep: [keys[0].ToUpperInvariant()])).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Photos", spelled.Text("field"));
        var tooMany = await (await api.SendCorrectionAsync(owner, lot.Id(), new HarvestFields { Photos = Enumerable.Repeat(Jpeg, 10).ToList() }, keep: keys)).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Photos", "A lot holds at most 10 photos."), (tooMany.Text("field"), tooMany.Text("hint")));
        var wrong = await (await api.SendCorrectionAsync(owner, lot.Id(), new HarvestFields { Photos = [Pdf] }, keep: keys)).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Photos", wrong.Text("field"));
        var part = await (await api.SendCorrectionAsync(owner, lot.Id(), new HarvestFields { PhotoField = "picture", Photos = [Jpeg] }, keep: keys)).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Photos", "Send each photo in a part named photos."), (part.Text("field"), part.Text("hint")));

        // A refused field or an old version stores nothing either.
        await (await api.SendCorrectionAsync(owner, lot.Id(), new HarvestFields { Cages = "0", Photos = [Jpeg] }, keep: keys)).ReadAsync(HttpStatusCode.BadRequest);
        await (await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields { Photos = [Jpeg] }, "x", AcsTestKit.StaleRowVersion, keys))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(stored, StoredKeys(api, Category));

        // Nine kept and one new is the most a lot holds.
        var saved = (await api.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Photos = Enumerable.Repeat(Jpeg, 9).ToList() }, keep: keys)).GetProperty("lot");
        Assert.Equal(10, saved.PhotoKeysOfLot().Count);
    }

    [Fact]
    public async Task A_removal_takes_the_photos_of_the_lot_from_the_store_and_leaves_those_of_other_lots()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var other = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["2"], Photos = [Png] });
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg, Png] });

        await api.RemoveLotAsync(owner, lot.Id());

        Assert.Equal(other.PhotoKeysOfLot(), StoredKeys(api, Category)!);
        Assert.Equal(1, await api.QueryAsync(db => db.HarvestLotPhotos.CountAsync()));
    }

    // The clean-up runs on a database that once held a lot, also when none is left: an orphan goes, and a photo a correction kept stays.
    [Fact]
    public async Task The_orphan_file_clean_up_keeps_corrected_photos_and_still_sweeps_after_the_last_lot_is_removed()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });
        var saved = (await api.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Photos = [Png] }, keep: lot.PhotoKeysOfLot())).GetProperty("lot");
        var job = api.Services.GetRequiredService<OrphanFileCleanupJob>();
        await StoreOrphanAsync(api);

        await job.RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2));
        Assert.Equal(saved.PhotoKeysOfLot().Order(), StoredKeys(api, Category)!);

        await api.RemoveLotAsync(owner, lot.Id());
        await StoreOrphanAsync(api);
        await job.RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2));
        Assert.Empty(StoredKeys(api, Category));
    }

    private static async Task StoreOrphanAsync(AuthApiFactory api)
    {
        var validated = FileValidator.Validate([new IncomingFile("file", Png.Length, new MemoryStream(Png))], FileRules.Photos);
        Assert.True((await api.Services.GetRequiredService<IFileStorage>().UploadAsync(validated.Files[0], Category)).Succeeded);
    }

    [Fact]
    public async Task A_photo_that_cannot_be_uploaded_changes_nothing_and_keeps_the_old_photos()
    {
        using var api = new FileFaultApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });
        var stored = StoredKeys(api, Category).ToList();
        api.Faults.UploadsBeforeFailure = 1;

        var answer = await (await api.SendCorrectionAsync(owner, lot.Id(), new HarvestFields { Cages = "9", Photos = [Jpeg, Png] })).ReadAsync(HttpStatusCode.BadGateway);

        Assert.Equal(("MSG-GEN-03", "MSG05"), (answer.Text("code"), answer.Text("legacyCode")));
        Assert.Equal(stored, StoredKeys(api, Category));
        var read = await owner.GetHarvestLotAsync(lot.Id());
        Assert.Equal((8, lot.PhotoKeysOfLot()[0]), (read.GetProperty("cageCount").GetInt32(), read.PhotoKeysOfLot()[0]));
    }

    [Fact]
    public async Task A_save_that_conflicts_after_the_uploads_answers_msg06_removes_them_and_keeps_the_old_photo()
    {
        using var api = new FileFaultApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });
        var stored = StoredKeys(api, Category).ToList();
        api.Faults.FailNextSave(Conflict);

        var answer = await api.SendCorrectionAsync(owner, lot.Id(), new HarvestFields { Photos = [Png] });

        Assert.Equal("MSG06", await answer.ErrorCodeAsync());
        Assert.Equal(stored, StoredKeys(api, Category));
    }

    /// <summary>Sends a correction whose first photo stops after it is stored, until released.</summary>
    private static async Task<Task<HttpResponseMessage>> HeldAsync(CycleLogsQaFaultFactory api, HttpClient client, Guid lotId, HarvestFields fields, IReadOnlyList<string>? keep = null)
    {
        var version = await api.LotVersionAsync(lotId);
        api.Hold.Arm();
        var pending = client.PutHarvestLotAsync(lotId, new HarvestCorrection(fields with { Photos = [Jpeg] }, "Wrong count", version, keep));
        await api.Hold.Reached;
        return pending;
    }

    [Fact]
    public async Task A_correction_saved_during_the_upload_makes_this_one_stale_and_removes_its_photos()
    {
        using var api = new CycleLogsQaFaultFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId);

        var pending = await HeldAsync(api, owner, lot.Id(), new HarvestFields { Cages = "9" });
        await api.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Cages = "7" });
        api.Hold.Release();

        Assert.Equal("MSG06", await (await pending).ErrorCodeAsync());
        Assert.Empty(StoredKeys(api, Category));
        Assert.Equal(7, (await owner.GetHarvestLotAsync(lot.Id())).GetProperty("cageCount").GetInt32());
    }

    [Fact]
    public async Task A_lot_removed_during_the_upload_answers_404_and_removes_the_photos()
    {
        using var api = new CycleLogsQaFaultFactory();
        var raft = await api.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId);

        var pending = await HeldAsync(api, raft.Owner.Client, lot.Id(), new HarvestFields());
        await api.RemoveLotAsync(raft.Owner.Client, lot.Id());
        api.Hold.Release();

        Assert.Equal(HttpStatusCode.NotFound, (await pending).StatusCode);
        Assert.Empty(StoredKeys(api, Category));
    }

    [Fact]
    public async Task A_lane_finished_by_another_lot_during_the_upload_refuses_and_removes_the_photos()
    {
        using var api = new CycleLogsQaFaultFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId);

        var pendingLane = await HeldAsync(api, owner, lot.Id(), HarvestFields.OfLanes(1, 2));
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2).FinishedLanes(2));
        api.Hold.Release();
        var refused = await (await pendingLane).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Lanes", refused.Text("field"));
        Assert.Empty(StoredKeys(api, Category));
    }

    [Fact]
    public async Task A_cycle_closed_during_the_upload_answers_msg_gen_09_and_removes_the_photos()
    {
        using var api = new CycleLogsQaFaultFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId);

        var pending = await HeldAsync(api, owner, lot.Id(), new HarvestFields());
        await api.UpdateAsync<CultivationCycle>(raft.CycleId, c =>
        {
            c.StatusId = OMTS.Domain.Constants.StatusIds.CultivationCycle.Closed;
            c.CloseDate = Today;
        });
        api.Hold.Release();

        Assert.Equal("MSG-GEN-09", await (await pending).ErrorCodeAsync());
        Assert.Empty(StoredKeys(api, Category));
    }

    [Fact]
    public async Task A_photo_to_keep_removed_during_the_upload_is_refused_and_removes_the_new_one()
    {
        using var api = new CycleLogsQaFaultFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Png] });
        var keys = lot.PhotoKeysOfLot();

        var pending = await HeldAsync(api, owner, lot.Id(), new HarvestFields(), keep: keys);
        // Only the photo row goes, with the version the held request read still current: so it is judged on the key. A real correction also moves the version, and that answers MSG06 (HarvestCorrectionsQaTestsFiles, M1).
        await api.UseDbAsync(db => db.HarvestLotPhotos.RemoveRange(db.HarvestLotPhotos.Where(p => p.LotId == lot.Id())));
        api.Hold.Release();

        var refused = await (await pending).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Photos", "Keep only photos of this lot."), (refused.Text("field"), refused.Text("hint")));
    }
}
