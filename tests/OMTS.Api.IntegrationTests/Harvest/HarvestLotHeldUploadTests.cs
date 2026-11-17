using System.Net;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Files;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Farming.FarmingUploadedFileTests;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>The minutes of an upload at sea: what the save decides is read again after it, and what it refuses leaves no photo behind.</summary>
public class HarvestLotHeldUploadTests
{
    private const string Category = FileCategories.HarvestLotPhotos;

    /// <summary>Sends a harvest whose first photo stops after it is stored, until released.</summary>
    private static async Task<Task<HttpResponseMessage>> HeldAsync(CycleLogsQaFaultFactory api, HttpClient client, Guid raftId, HarvestFields fields)
    {
        api.Hold.Arm();
        var pending = client.PostHarvestAsync(raftId, fields with { Photos = [Jpeg] });
        await api.Hold.Reached;
        return pending;
    }

    // BR-HARVEST-02: "When a lot is recorded": the moment of the save.
    [Fact]
    public async Task A_setting_saved_during_the_upload_is_the_one_copied_and_used_for_the_estimate()
    {
        using var api = new CycleLogsQaFaultFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;

        var pending = await HeldAsync(api, owner, raft.RaftId, new HarvestFields { Cages = "8" });
        await owner.SetHarvestSettingsAsync(62.5m, 3);
        api.Hold.Release();

        var lot = (await (await pending).ReadAsync(HttpStatusCode.Created)).GetProperty("lot");
        Assert.Equal((62.5m, 500m), (lot.Kg("kgPerCage"), lot.Kg("estimatedKg")));
    }

    [Fact]
    public async Task A_lot_saved_during_the_upload_refuses_nothing_and_this_one_takes_the_next_code_and_sees_the_lanes_it_left()
    {
        using var api = new CycleLogsQaFaultFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var manager = await api.ManagerAsync(raft.ZoneId);

        var pending = await HeldAsync(api, manager.Client, raft.RaftId, HarvestFields.OfLanes(1, 2));
        var between = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        api.Hold.Release();

        var lot = (await (await pending).ReadAsync(HttpStatusCode.Created)).GetProperty("lot");
        Assert.Equal($"{between.Text("lotCode")}-2", lot.Text("lotCode"));
        Assert.Single(lot.GetProperty("photos").EnumerateArray());
    }

    [Fact]
    public async Task A_lane_finished_during_the_upload_answers_400_and_removes_the_photos()
    {
        using var api = new CycleLogsQaFaultFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;

        var pending = await HeldAsync(api, owner, raft.RaftId, HarvestFields.OfLanes(1, 2));
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));
        api.Hold.Release();

        var refused = await (await pending).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Lanes", "Lanes 1 cannot be harvested now."), (refused.Text("field"), refused.Text("hint")));
        await api.AssertNoOrphanLotPhotosAsync(Category);
        Assert.Equal(1, await api.LotCountAsync(raft.RaftId));
    }

    [Fact]
    public async Task A_cycle_closed_during_the_upload_answers_msg_gen_09_tells_nobody_and_removes_the_photos()
    {
        using var api = new CycleLogsQaFaultFactory();
        var raft = await api.SeededRaftAsync();
        var manager = await api.ManagerAsync(raft.ZoneId);

        var pending = await HeldAsync(api, manager.Client, raft.RaftId, HarvestFields.OfLanes(1));
        await api.CloseCycleAsync(raft.CycleId);
        api.Hold.Release();

        await (await pending).ReadAsync(HttpStatusCode.Conflict);
        await api.AssertNoOrphanLotPhotosAsync(Category);
        Assert.Equal(0, await api.LotCountAsync(raft.RaftId));
        Assert.Equal(0, await api.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == "HARVEST_LOT_RECORDED")));
    }

    [Fact]
    public async Task A_raft_discontinued_during_the_upload_answers_msg_gen_09()
    {
        using var api = new CycleLogsQaFaultFactory();
        var raft = await api.SeededRaftAsync();

        var pending = await HeldAsync(api, raft.Owner.Client, raft.RaftId, HarvestFields.OfLanes(1));
        await api.DiscontinueRaftAsync(raft.RaftId);
        api.Hold.Release();

        Assert.Equal("MSG-GEN-09", await (await pending).ErrorCodeAsync());
        await api.AssertNoOrphanLotPhotosAsync(Category);
    }

    // A failure between the uploads and the save (here the notification rows): no row will point to the files, so they do not wait for the clean-up.
    [Fact]
    public async Task A_failure_before_the_save_removes_the_uploads()
    {
        using var api = new CycleLogsQaFaultFactory();
        var raft = await api.SeededRaftAsync();
        var manager = await api.ManagerAsync(raft.ZoneId);
        api.NotifyFails = true;

        var answer = await manager.Client.PostHarvestAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg, Png] });

        Assert.Equal(HttpStatusCode.InternalServerError, answer.StatusCode);
        await api.AssertNoOrphanLotPhotosAsync(Category);
        Assert.Equal(0, await api.LotCountAsync(raft.RaftId));
    }
}
