using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using OMTS.Infrastructure.Storage;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Farming.FarmingUploadedFileTests;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// NFR-09 on real row versions: a form read before another save gets MSG06 with the new version, whatever it still says of the photos and lanes that save changed.
/// With the current version the same values are judged, and a bad one answers MSG-GEN-01.
/// </summary>
public class HarvestLotStaleFormSqlServerTests
{
    private static async Task<(FarmingSqlServerHost Host, HttpClient Owner, Guid RaftId)> StartAsync()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var raftId = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleId, (await owner.SeedLotAsync()).Session([1, 2, 3]));
        return (host, owner, raftId);
    }

    private static void AssertStale(JsonElement answer, JsonElement saved) =>
        Assert.Equal(("MSG06", saved.GetProperty("lot").Text("rowVersion")), (answer.Text("code"), answer.Text("currentRowVersion")));

    [SqlServerFact]
    public async Task A_form_keeping_a_photo_another_save_removed_gets_msg06_and_a_bad_key_on_the_current_version_gets_msg_gen_01()
    {
        var (host, owner, raftId) = await StartAsync();
        await using var _ = host;
        var lot = await owner.HarvestLotAsync(raftId, new HarvestFields { Lanes = ["1"], Photos = [Jpeg, Png] });
        var keys = lot.PhotoKeysOfLot();
        var saved = await (await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields { Lanes = ["1"] }, "Wrong photo", lot.Text("rowVersion"), [keys[0]]))).ReadAsync(HttpStatusCode.OK);

        var stale = await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields { Lanes = ["1"], Cages = "9" }, "Counted again", lot.Text("rowVersion"), keys));
        AssertStale(await stale.ReadAsync(HttpStatusCode.Conflict), saved);

        var current = saved.GetProperty("lot").Text("rowVersion");
        var badKey = await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields { Lanes = ["1"], Cages = "9" }, "Counted again", current, keys));
        var refused = await badKey.ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Photos", "Keep only photos of this lot."), (refused.Text("code"), refused.Text("field"), refused.Text("hint")));
        Assert.Equal(current, (await owner.GetHarvestLotAsync(lot.Id())).Text("rowVersion"));
    }

    [SqlServerFact]
    public async Task A_form_marking_a_lane_another_save_took_out_gets_msg06_and_a_bad_lane_on_the_current_version_gets_msg_gen_01()
    {
        var (host, owner, raftId) = await StartAsync();
        await using var _ = host;
        var lot = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1, 2).FinishedLanes(1));
        var saved = await (await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(HarvestFields.OfLanes(2), "Wrong lane", lot.Text("rowVersion")))).ReadAsync(HttpStatusCode.OK);
        await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1).FinishedLanes(1));
        var form = HarvestFields.OfLanes(1, 2).FinishedLanes(1) with { Cages = "9" };

        var stale = await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(form, "Counted again", lot.Text("rowVersion")));
        AssertStale(await stale.ReadAsync(HttpStatusCode.Conflict), saved);

        var badLane = await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(form, "Counted again", saved.GetProperty("lot").Text("rowVersion")));
        var refused = await badLane.ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Lanes"), (refused.Text("code"), refused.Text("field")));
    }

    [SqlServerFact]
    public async Task A_correction_during_an_upload_answers_msg06_to_the_one_uploading_and_its_file_is_removed()
    {
        var (host, owner, raftId) = await StartAsync();
        await using var _ = host;
        var hold = new UploadHold();
        await using var held = host.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IFileStorage>(provider => new HeldStorage(provider.GetRequiredService<LocalFileStorage>(), hold))));
        var uploading = held.CreateClient();
        var tokens = await AcsTestKit.LoginTokensAsync(host.Api.CreateClient(), host.Owner.Account.Username);
        uploading.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        var lot = await owner.HarvestLotAsync(raftId, new HarvestFields { Lanes = ["1"], Photos = [Jpeg, Png] });
        var keys = lot.PhotoKeysOfLot();
        hold.Arm();

        var pending = uploading.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields { Lanes = ["1"], Photos = [Jpeg] }, "One more", lot.Text("rowVersion"), keys));
        await hold.Reached;
        var saved = await (await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields { Lanes = ["1"] }, "Wrong photo", lot.Text("rowVersion"), [keys[0]]))).ReadAsync(HttpStatusCode.OK);
        hold.Release();

        AssertStale(await (await pending).ReadAsync(HttpStatusCode.Conflict), saved);
        Assert.Equal([keys[0]], host.StoredKeys(FileCategories.HarvestLotPhotos));
    }
}
