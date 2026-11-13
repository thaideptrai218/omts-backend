using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// The cages harvested match the purchase request (owner decision of 13/11/2026): the lots of a request never add up to more than it accepted, a request with all its cages (15/15)
/// leaves the list and the saved lot says so, while marking the harvest done stays the Owner's act.
/// </summary>
public sealed class HarvestOrderCagesTests
{
    private static async Task<JsonElement[]> OffersAsync(HttpClient owner, Guid raftId) =>
        (await owner.HarvestScreenAsync(raftId)).GetProperty("orders").EnumerateArray().ToArray();

    [Fact]
    public async Task Lots_cannot_exceed_the_accepted_cages_the_last_exact_lot_completes_the_request_and_it_leaves_the_list()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var order = await HarvestOrderKit.OrderAsync(api.Services, acceptedCages: 15);

        // More than the order holds: refused on the cages field, nothing stored.
        var tooMany = await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields { Cages = "16" }.For(order))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-09", "HARVEST_ORDER_CAGES_EXCEEDED", "CageCount"), (tooMany.Text("code"), tooMany.Text("reasonCode"), tooMany.Text("field")));
        Assert.Contains("chỉ còn 15 lồng (đã thu 0/15 lồng)", tooMany.Text("detail"));
        Assert.Contains("Số lồng thu hoạch phải khớp với đơn mua", tooMany.Text("detail"));
        Assert.Equal(0, await api.LotCountAsync(raft.RaftId));

        var first = await owner.RecordHarvestAsync(raft.RaftId, new HarvestFields { Lanes = ["1"], Cages = "10" }.For(order));
        Assert.Equal((15, 10m, false), (first.GetProperty("orderCagesTotal").GetInt32(), first.GetProperty("orderCagesHarvested").GetDecimal(), first.GetProperty("orderComplete").GetBoolean()));
        var offered = Assert.Single(await OffersAsync(owner, raft.RaftId), o => o.Id() == order);
        Assert.Equal((15, 10m), (offered.GetProperty("acceptedCages").GetInt32(), offered.GetProperty("harvestedCages").GetDecimal()));
        // Where the cages came from, over every raft and zone: here one raft with its 10 cages.
        var trip = Assert.Single(offered.GetProperty("trips").EnumerateArray());
        Assert.Equal(10m, trip.GetProperty("cages").GetDecimal());
        Assert.False(string.IsNullOrEmpty(trip.Text("raftCode")));
        Assert.False(string.IsNullOrEmpty(trip.Text("zoneCode")));

        // The remaining cages are the most a lot takes: half a cage over is refused.
        var over = await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields { Lanes = ["2"], Cages = "5.5" }.For(order))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("HARVEST_ORDER_CAGES_EXCEEDED", over.Text("reasonCode"));
        Assert.Contains("chỉ còn 5 lồng (đã thu 10/15 lồng)", over.Text("detail"));

        // Exactly what is left is accepted and completes the request, but the harvest is not marked done by it.
        var last = await owner.RecordHarvestAsync(raft.RaftId, new HarvestFields { Lanes = ["2"], Cages = "5" }.For(order));
        Assert.Equal((15, 15m, true), (last.GetProperty("orderCagesTotal").GetInt32(), last.GetProperty("orderCagesHarvested").GetDecimal(), last.GetProperty("orderComplete").GetBoolean()));
        Assert.DoesNotContain(await OffersAsync(owner, raft.RaftId), o => o.Id() == order);
        Assert.Equal(2, await api.LotCountAsync(raft.RaftId));
        Assert.Null((await api.QueryAsync(db => db.PurchaseRequests.SingleAsync(r => r.Id == order))).HarvestCompletedAt);

        // A filled request takes no more, and a correction cannot raise a lot over it, but may lower one.
        Assert.Equal("HARVEST_ORDER_CAGES_EXCEEDED", (await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields { Lanes = ["3"], Cages = "1" }.For(order))).ReadAsync(HttpStatusCode.BadRequest)).Text("reasonCode"));
        var firstLot = first.GetProperty("lot").Id();
        var raised = await (await api.SendCorrectionAsync(owner, firstLot, HarvestFields.OfLanes(1) with { Cages = "11" })).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("HARVEST_ORDER_CAGES_EXCEEDED", "CageCount"), (raised.Text("reasonCode"), raised.Text("field")));
        Assert.Contains("chỉ còn 10 lồng (đã thu 5/15 lồng)", raised.Text("detail"));
        var lowered = await api.CorrectLotAsync(owner, firstLot, HarvestFields.OfLanes(1) with { Cages = "9" });
        Assert.Equal(9m, lowered.GetProperty("lot").GetProperty("cageCount").GetDecimal());
    }
}
