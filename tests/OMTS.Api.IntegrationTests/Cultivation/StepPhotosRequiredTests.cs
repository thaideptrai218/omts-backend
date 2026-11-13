using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// Owner decision of 12/11/2026: a new seeding session, harvest lot, cycle plan and lane damage report carry 1 to 3 on-site photos; an older record without photos stays valid and
/// readable; an edit leaves 1 to 3 when the record has photos; a care log's photos are optional, at most 3. Reads give <c>photos[{id,url}]</c>.
/// </summary>
public class StepPhotosRequiredTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static List<Guid> PhotoIds(JsonElement record, string property = "photos") => record.GetProperty(property).EnumerateArray().Select(p => p.Id()).ToList();

    [Fact]
    public async Task A_seeding_session_needs_1_to_3_photos_and_reads_them_back_with_an_id_and_a_link()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(1000m);
        var filesBefore = factory.StoredFiles("seeding-photos").Count;

        var none = await (await owner.PostSeedingAsync(setup.CycleId, lot.Session([1]), photos: 0)).ReadAsync(HttpStatusCode.BadRequest);
        var four = await (await owner.PostSeedingAsync(setup.CycleId, lot.Session([1]), photos: 4)).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Photos", "Photos"), (none.Text("code"), none.Text("field"), four.Text("field")));
        Assert.Equal(0, await factory.QueryAsync(db => db.SeedingRecords.CountAsync(r => r.CycleId == setup.CycleId)));

        var saved = (await (await owner.PostSeedingAsync(setup.CycleId, lot.Session([1]), photos: 3)).ReadAsync(HttpStatusCode.Created)).GetProperty("record");

        Assert.Equal(3, PhotoIds(saved).Count);
        Assert.All(saved.GetProperty("photos").EnumerateArray(), p => Assert.False(string.IsNullOrEmpty(p.Text("url"))));
        Assert.Equal(filesBefore + 3, factory.StoredFiles("seeding-photos").Count);
        Assert.Equal(3, PhotoIds(await owner.GetSeedingAsync(saved.Id())).Count);
    }

    [Fact]
    public async Task A_seeding_correction_keeps_1_to_3_photos_and_an_older_session_without_photos_stays_valid()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(1000m);
        var saved = (await (await owner.PostSeedingAsync(setup.CycleId, lot.Session([1]), photos: 2)).ReadAsync(HttpStatusCode.Created)).GetProperty("record");
        var ids = PhotoIds(saved);

        var kept = (await owner.CorrectSeedingPhotosAsync(saved, keep: [ids[0]], photos: 1)).GetProperty("record");
        Assert.Equal(2, PhotoIds(kept).Count);
        Assert.Contains(ids[0], PhotoIds(kept));
        Assert.DoesNotContain(ids[1], PhotoIds(kept));

        var none = await owner.CorrectSeedingPhotosAsync(kept, keep: [], photos: 0, HttpStatusCode.BadRequest);
        Assert.Equal("Photos", none.Text("field"));
        var four = await owner.CorrectSeedingPhotosAsync(kept, keep: PhotoIds(kept), photos: 2, HttpStatusCode.BadRequest);
        Assert.Equal("Photos", four.Text("field"));

        await factory.QueryAsync(async db =>
        {
            db.SeedingRecordPhotos.RemoveRange(db.SeedingRecordPhotos.Where(p => p.SeedingRecordId == saved.Id()));
            await db.SaveChangesAsync();
            return 0;
        });
        var legacy = await owner.GetSeedingAsync(saved.Id());
        Assert.Empty(PhotoIds(legacy));
        var corrected = (await (await owner.PutSeedingAsync(saved.Id(), legacy.AsCorrection("Fix the quantity") with { Quantity = 11m })).ReadAsync(HttpStatusCode.OK)).GetProperty("record");
        Assert.Equal((11m, 0), (corrected.GetProperty("quantity").GetDecimal(), PhotoIds(corrected).Count));
    }

    [Fact]
    public async Task A_harvest_lot_needs_1_to_3_photos_and_an_older_lot_without_photos_can_be_read_and_corrected()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;

        var none = await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields { NoPhoto = true })).ReadAsync(HttpStatusCode.BadRequest);
        var four = await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields { Photos = [FarmingTestKit.Jpeg, FarmingTestKit.Jpeg, FarmingTestKit.Jpeg, FarmingTestKit.Jpeg] })).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Photos", "Photos"), (none.Text("field"), four.Text("field")));
        Assert.Equal(0, await factory.LotCountAsync(raft.RaftId));

        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [FarmingTestKit.Jpeg, FarmingTestKit.Jpeg] });
        Assert.Equal(2, PhotoIds(lot).Count);
        Assert.All(lot.GetProperty("photos").EnumerateArray(), p => Assert.False(string.IsNullOrEmpty(p.Text("url"))));

        var emptied = await (await factory.SendCorrectionAsync(owner, lot.Id(), HarvestFields.OfLanes(1), keep: [])).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Photos", emptied.Text("field"));

        await factory.QueryAsync(async db =>
        {
            db.HarvestLotPhotos.RemoveRange(db.HarvestLotPhotos.Where(p => p.LotId == lot.Id()));
            await db.SaveChangesAsync();
            return 0;
        });
        Assert.Empty(PhotoIds(await owner.GetHarvestLotAsync(lot.Id())));
        var corrected = await (await factory.SendCorrectionAsync(owner, lot.Id(), HarvestFields.OfLanes(1) with { Cages = "9" })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((9m, 0), (corrected.GetProperty("lot").Kg("cageCount"), PhotoIds(corrected.GetProperty("lot")).Count));
    }

    [Fact]
    public async Task A_cycle_started_without_a_nursery_batch_needs_1_to_3_plan_photos_that_can_be_changed_within_1_to_3()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var raftId = (await owner.Client.CreateRaftAsync(zoneId)).Id();
        var filesBefore = factory.StoredFiles("cycle-plan-photos").Count;

        var none = await (await owner.Client.PostCycleAsync(raftId, new CycleBody(FarmingTestKit.Today.AddDays(-3)), photos: 0)).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Photos", none.Text("field"));
        Assert.False(await factory.QueryAsync(db => db.CultivationCycles.AnyAsync(c => c.RaftId == raftId)));
        Assert.Equal(filesBefore, factory.StoredFiles("cycle-plan-photos").Count);

        var cycle = (await (await owner.Client.PostCycleAsync(raftId, new CycleBody(FarmingTestKit.Today.AddDays(-3)), photos: 2)).ReadAsync(HttpStatusCode.Created)).GetProperty("cycle");
        Assert.Equal((2, false), (PhotoIds(cycle, "planPhotos").Count, cycle.Flag("planPhotosFromNursery")));
        var ids = PhotoIds(cycle, "planPhotos");
        var version = await factory.IssueRowVersionAsync<OMTS.Domain.Entities.Cultivation.CultivationCycle>(cycle.Id());

        var form = StepFormKit.FormOf(new { rowVersion = version, keepPhotoIds = new[] { ids[0] } }, photos: 1);
        var changed = (await (await owner.Client.PutAsync($"/api/cultivation-cycles/{cycle.Id()}/plan-photos", form)).ReadAsync(HttpStatusCode.OK)).GetProperty("cycle");
        Assert.Equal(2, PhotoIds(changed, "planPhotos").Count);
        Assert.DoesNotContain(ids[1], PhotoIds(changed, "planPhotos"));

        var emptied = StepFormKit.FormOf(new { rowVersion = await factory.IssueRowVersionAsync<OMTS.Domain.Entities.Cultivation.CultivationCycle>(cycle.Id()) }, photos: 0);
        Assert.Equal("Photos", (await (await owner.Client.PutAsync($"/api/cultivation-cycles/{cycle.Id()}/plan-photos", emptied)).ReadAsync(HttpStatusCode.BadRequest)).Text("field"));
    }

    // v2 (13/11/2026): a cycle started from a nursery batch has plan photos of its own (1 to 3, required), changed within 1 to 3 like any other; none of them comes from the batch.
    [Fact]
    public async Task A_cycle_started_from_a_nursery_batch_has_its_own_plan_photos_that_can_be_changed_within_1_to_3()
    {
        var world = await factory.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.ReadyNurseryAsync(factory, world.ZoneId, world.Lot, "1000", "900");

        var none = await (await client.PostNurseryCycleAsync(world.Raft1, batch.Id(), photos: 0)).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Photos", none.Text("field"));
        var cycle = (await (await client.PostNurseryCycleAsync(world.Raft1, batch.Id(), photos: 2)).ReadAsync(HttpStatusCode.Created)).GetProperty("cycle");

        Assert.Equal((2, false), (PhotoIds(cycle, "planPhotos").Count, cycle.Flag("planPhotosFromNursery")));
        var ids = PhotoIds(cycle, "planPhotos");
        var changed = StepFormKit.FormOf(new { rowVersion = await factory.IssueRowVersionAsync<OMTS.Domain.Entities.Cultivation.CultivationCycle>(cycle.Id()), keepPhotoIds = new[] { ids[0] } }, photos: 1);
        var after = (await (await client.PutAsync($"/api/cultivation-cycles/{cycle.Id()}/plan-photos", changed)).ReadAsync(HttpStatusCode.OK)).GetProperty("cycle");
        Assert.Equal(2, PhotoIds(after, "planPhotos").Count);
        Assert.Contains(ids[0], PhotoIds(after, "planPhotos"));
    }
    [Fact]
    public async Task A_lane_damage_report_needs_its_note_and_1_to_3_photos_and_keeps_them_with_the_incident()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var url = $"/api/rafts/{raft.RaftId}/lanes/3/report-damage";
        var filesBefore = factory.StoredFiles("lane-incident-photos").Count;
        var version = (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}")).Text("rowVersion");

        var noPhoto = await (await owner.PostAsync(url, StepFormKit.FormOf(new { reason = "Storm took the line at noon", rowVersion = version }, photos: 0))).ReadAsync(HttpStatusCode.BadRequest);
        var fourPhotos = await (await owner.PostAsync(url, StepFormKit.FormOf(new { reason = "Storm took the line at noon", rowVersion = version }, photos: 4))).ReadAsync(HttpStatusCode.BadRequest);
        var noNote = await (await owner.PostAsync(url, StepFormKit.FormOf(new { reason = " ", rowVersion = version }, photos: 1))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Photos", "Photos", "Reason"), (noPhoto.Text("field"), fourPhotos.Text("field"), noNote.Text("field")));
        Assert.False(await factory.QueryAsync(db => db.Lanes.AnyAsync(l => l.RaftId == raft.RaftId && l.IsDamaged)));
        Assert.Equal(filesBefore, factory.StoredFiles("lane-incident-photos").Count);

        var saved = await (await owner.PostAsync(url, StepFormKit.FormOf(new { reason = "Storm took the line at noon", rowVersion = version }, photos: 3))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(3, saved.GetProperty("incident").GetProperty("photos").GetArrayLength());
        Assert.Equal(filesBefore + 3, factory.StoredFiles("lane-incident-photos").Count);
    }

    [Fact]
    public async Task The_photos_of_a_care_log_are_optional_and_at_most_three()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;

        var without = await (await owner.PostLogAsync(setup.CycleId, new LogFields { Photos = [] })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(0, without.GetProperty("log").GetProperty("photos").GetArrayLength());

        var three = await (await owner.PostLogAsync(setup.CycleId, new LogFields { Photos = [FarmingTestKit.Jpeg, FarmingTestKit.Jpeg, FarmingTestKit.Jpeg] })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(3, three.GetProperty("log").GetProperty("photos").GetArrayLength());

        var four = await (await owner.PostLogAsync(setup.CycleId, new LogFields { Photos = [FarmingTestKit.Jpeg, FarmingTestKit.Jpeg, FarmingTestKit.Jpeg, FarmingTestKit.Jpeg] })).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Photos", four.Text("field"));
    }
}

/// <summary>A seeding correction that sends photos, the answer's to read.</summary>
internal static class SeedingPhotoCorrectionKit
{
    public static async Task<JsonElement> CorrectSeedingPhotosAsync(
        this HttpClient client, JsonElement record, IReadOnlyList<Guid> keep, int photos, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = record.AsCorrection("Photos changed");
        var form = StepFormKit.FormOf(body, photos);
        foreach (var id in keep)
        {
            form.Add(new StringContent(id.ToString()), "keepPhotoIds");
        }

        if (keep.Count == 0 && photos == 0)
        {
            // An empty list cannot travel in a form: a lone blank id says "keep none", as the web does.
            form.Add(new StringContent(Guid.Empty.ToString()), "keepPhotoIds");
        }

        return await (await client.PutAsync($"/api/seeding-records/{record.Id()}", form)).ReadAsync(expected);
    }
}
