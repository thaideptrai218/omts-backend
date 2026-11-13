using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>Decision of 11/11/2026: a harvest trip is recorded for one purchase request, Accepted and whose harvest is not marked done yet.</summary>
public class HarvestLotOrderTests
{
    [Fact]
    public async Task A_lot_names_the_request_it_harvests_for()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;

        Assert.Equal(["Purchase request"], await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields { WithoutOrder = true })).InvalidFieldsAsync());
        Assert.Equal(["Purchase request", "Number of cages"], await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields { WithoutOrder = true, Cages = "0" })).InvalidFieldsAsync());
        Assert.Equal(["Purchase request"], await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields().For(Guid.NewGuid()))).InvalidFieldsAsync());
        Assert.Equal(0, await api.LotCountAsync(raft.RaftId));

        var order = await api.HarvestOrderAsync();
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields().For(order));

        var code = await api.QueryAsync(db => db.PurchaseRequests.Where(r => r.Id == order).Select(r => r.Code).SingleAsync());
        Assert.Equal((order, code), (lot.GetProperty("purchaseRequestId").GetGuid(), lot.Text("purchaseRequestCode")));
        Assert.Equal(code, (await owner.HarvestScreenAsync(raft.RaftId)).GetProperty("lots")[0].Text("purchaseRequestCode"));
        Assert.Equal(code, (await owner.GetHarvestLotAsync(lot.Id())).Text("purchaseRequestCode"));
    }

    [Fact]
    public async Task Only_an_accepted_request_whose_harvest_is_not_done_takes_a_lot()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        Guid[] refused =
        [
            await HarvestOrderKit.OrderAsync(api.Services, StatusIds.PurchaseRequest.AwaitingResponse),
            await HarvestOrderKit.OrderAsync(api.Services, StatusIds.PurchaseRequest.Declined),
            await HarvestOrderKit.OrderAsync(api.Services, StatusIds.PurchaseRequest.Cancelled),
            await HarvestOrderKit.OrderAsync(api.Services, harvestDone: true),
        ];

        foreach (var order in refused)
        {
            Assert.Equal("MSG-GEN-09", await (await raft.Owner.Client.PostHarvestAsync(raft.RaftId, new HarvestFields().For(order))).ErrorCodeAsync());
        }

        Assert.Equal(0, await api.LotCountAsync(raft.RaftId));
    }

    [Fact]
    public async Task The_form_offers_the_open_requests_with_the_cages_already_harvested_for_them()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var open = await api.HarvestOrderAsync();
        await HarvestOrderKit.OrderAsync(api.Services, harvestDone: true);
        await HarvestOrderKit.OrderAsync(api.Services, StatusIds.PurchaseRequest.AwaitingResponse);
        await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = "12" }.For(open));
        await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["2"], Cages = "3" }.For(open));

        var offered = Assert.Single((await owner.HarvestScreenAsync(raft.RaftId)).GetProperty("orders").EnumerateArray());

        Assert.Equal((open, 15, 1000), (offered.Id(), offered.GetProperty("harvestedCages").GetInt32(), offered.GetProperty("acceptedCages").GetInt32()));
        Assert.False(offered.GetProperty("plannedOnRaft").GetBoolean());
        Assert.StartsWith("Thương lái thu hoạch", offered.Text("customerName"));
    }
}
