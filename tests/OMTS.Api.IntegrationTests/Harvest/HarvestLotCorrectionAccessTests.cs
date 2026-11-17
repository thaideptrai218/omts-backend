using System.Net;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>BR-HARVEST-05 and §I.4.4: a Zone Manager corrects and removes what they recorded while the cycle is Open, the Owner any lot; nobody else; and the commands say the same.</summary>
public class HarvestLotCorrectionAccessTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task AssertRefusedAsync(HttpClient client, Guid lotId, string code = "MSG-GEN-08", HttpStatusCode status = HttpStatusCode.Forbidden)
    {
        foreach (var answer in new[]
        {
            await factory.SendCorrectionAsync(client, lotId, HarvestFields.OfLanes(1)),
            await factory.SendRemovalAsync(client, lotId),
        })
        {
            Assert.Equal(status, answer.StatusCode);
            Assert.Equal(code, await answer.ErrorCodeAsync());
        }
    }

    [Fact]
    public async Task The_zone_manager_who_recorded_it_corrects_and_removes_it_and_the_commands_say_so()
    {
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId);
        Assert.True(lot.GetProperty("canCorrect").GetBoolean());
        Assert.True(lot.GetProperty("canRemove").GetBoolean());
        var item = (await manager.Client.HarvestScreenAsync(raft.RaftId)).GetProperty("lots")[0];
        Assert.True(item.GetProperty("canCorrect").GetBoolean());
        Assert.True(item.GetProperty("canRemove").GetBoolean());

        var saved = await factory.CorrectLotAsync(manager.Client, lot.Id(), HarvestFields.OfLanes(1, 2));
        Assert.Equal([1, 2], saved.GetProperty("lot").Numbers("laneNumbers"));
        await factory.RemoveLotAsync(manager.Client, lot.Id());
        Assert.Equal(0, await factory.LotCountAsync(raft.RaftId));
    }

    [Fact]
    public async Task The_owner_corrects_and_removes_any_lot_and_a_manager_of_all_zones_only_their_own()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var allZones = await factory.StaffAsync(null);
        var ofManager = await manager.Client.HarvestLotAsync(raft.RaftId);
        var ofAllZones = await allZones.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2));

        Assert.True((await owner.GetHarvestLotAsync(ofManager.Id())).GetProperty("canCorrect").GetBoolean());
        await factory.CorrectLotAsync(owner, ofManager.Id(), HarvestFields.OfLanes(1, 3));
        await factory.CorrectLotAsync(allZones.Client, ofAllZones.Id(), HarvestFields.OfLanes(2, 4));
        Assert.False((await allZones.Client.GetHarvestLotAsync(ofManager.Id())).GetProperty("canCorrect").GetBoolean());
        await AssertRefusedAsync(allZones.Client, ofManager.Id());

        await factory.RemoveLotAsync(owner, ofManager.Id());
        await factory.RemoveLotAsync(allZones.Client, ofAllZones.Id());
    }

    [Fact]
    public async Task Another_manager_of_the_zone_a_manager_elsewhere_an_accountant_and_a_trader_are_refused_and_nothing_changes()
    {
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId);
        var sameZone = await factory.ManagerAsync(raft.ZoneId);
        var elsewhere = await factory.ManagerAsync((await raft.Owner.Client.CreateZoneAsync()).Id());
        var zone = await factory.ZoneEntityAsync(raft.ZoneId);

        // The Owner's own lot too: a Zone Manager corrects only what they recorded.
        var ofOwner = await raft.Owner.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2));
        await AssertRefusedAsync(manager.Client, ofOwner.Id());
        await AssertRefusedAsync(sameZone.Client, lot.Id());
        Assert.False((await sameZone.Client.GetHarvestLotAsync(lot.Id())).GetProperty("canRemove").GetBoolean());
        await AssertRefusedAsync(elsewhere.Client, lot.Id());
        await AssertRefusedAsync((await factory.StaffAsync(zone, StaffRole.Accountant)).Client, lot.Id());
        await AssertRefusedAsync((await factory.StaffAsync(null, StaffRole.Accountant)).Client, lot.Id());
        await AssertRefusedAsync(await factory.TraderAsync(), lot.Id());
        Assert.Equal(2, await factory.LotCountAsync(raft.RaftId));
        Assert.Equal(8, (await manager.Client.GetHarvestLotAsync(lot.Id())).GetProperty("cageCount").GetDecimal());
    }

    // GFR-02: access first, so a refused caller learns nothing of the state or the form.
    [Fact]
    public async Task Access_comes_before_the_cycle_and_the_fields()
    {
        var raft = await factory.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId);
        await factory.CloseCycleAsync(raft.CycleId);
        var sameZone = await factory.ManagerAsync(raft.ZoneId);

        var answer = await factory.SendCorrectionAsync(sameZone.Client, lot.Id(), new HarvestFields { Cages = "0", PhotoField = "other", Photos = [FarmingTestKit.Jpeg] }, reason: null);

        Assert.Equal("MSG-GEN-08", await answer.ErrorCodeAsync());
    }

    [Fact]
    public async Task Anonymous_callers_get_401_and_an_unknown_lot_404()
    {
        var raft = await factory.SeededRaftAsync();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = null;

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutHarvestLotAsync(Guid.NewGuid(), new HarvestCorrection(new HarvestFields()))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostRemoveLotAsync(Guid.NewGuid(), "x", "y")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await raft.Owner.Client.PutHarvestLotAsync(Guid.NewGuid(), new HarvestCorrection(new HarvestFields(), RowVersion: AcsTestKit.StaleRowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await raft.Owner.Client.PostRemoveLotAsync(Guid.NewGuid(), "x", AcsTestKit.StaleRowVersion)).StatusCode);
    }

    [Fact]
    public async Task A_closed_cycle_refuses_with_msg_gen_09_also_for_the_owner_and_the_commands_go_off_and_the_lot_stays_readable()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId);

        await factory.CloseCycleAsync(raft.CycleId);

        await AssertRefusedAsync(owner, lot.Id(), "MSG-GEN-09", HttpStatusCode.Conflict);
        var read = await owner.GetHarvestLotAsync(lot.Id());
        Assert.False(read.GetProperty("canCorrect").GetBoolean());
        Assert.False(read.GetProperty("canRemove").GetBoolean());
    }

    [Fact]
    public async Task A_discontinued_raft_or_zone_refuses_with_msg_gen_09()
    {
        var raft = await factory.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId);

        await factory.DiscontinueRaftAsync(raft.RaftId);
        await AssertRefusedAsync(raft.Owner.Client, lot.Id(), "MSG-GEN-09", HttpStatusCode.Conflict);
        Assert.False((await raft.Owner.Client.GetHarvestLotAsync(lot.Id())).GetProperty("canCorrect").GetBoolean());
        await factory.UpdateAsync<Raft>(raft.RaftId, r => r.StatusId = Domain.Constants.StatusIds.Usage.InUse);

        await factory.UpdateAsync<FarmingZone>(raft.ZoneId, z => z.StatusId = Domain.Constants.StatusIds.Usage.Discontinued);
        await AssertRefusedAsync(raft.Owner.Client, lot.Id(), "MSG-GEN-09", HttpStatusCode.Conflict);
    }
}
