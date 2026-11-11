using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Sales.PortWeighingTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>Decision of 11/11/2026: the Cooperative Owner marks the harvest of an Accepted request done once its lots are in, and clears the mark while it has no weighing.</summary>
public class PurchaseHarvestDoneTests
{
    private static async Task<(AuthApiFactory Api, SaleSetup Sale, TraderActor Trader, JsonElement Request)> AcceptedAsync()
    {
        var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var trader = await api.TraderAsync();
        var request = await trader.Client.PlaceAsync(sale.ProductId, 20);
        await (await sale.Owner.Client.AcceptAsync(request)).ReadAsync(HttpStatusCode.OK);
        return (api, sale, trader, request);
    }

    private static bool Flag(JsonElement detail, string name) => detail.GetProperty("harvest").GetProperty(name).GetBoolean();

    [Fact]
    public async Task The_owner_marks_the_harvest_done_with_its_lots_and_no_lot_is_added_after()
    {
        var (api, sale, trader, request) = await AcceptedAsync();
        using (api)
        {
            var owner = sale.Owner.Client;
            var empty = await owner.RequestDetailAsync(request.Id());
            Assert.Equal((0, false, false), (empty.GetProperty("harvest").GetProperty("harvestedCages").GetInt32(), Flag(empty, "canMarkDone"), Flag(empty, "canUnmarkDone")));
            Assert.Equal("MSG-GEN-09", await (await owner.PostHarvestDoneAsync(request.Id(), empty.RowVersion())).ErrorCodeAsync());

            // A Zone Manager and the Owner each record a trip for it.
            await sale.Manager.Client.HarvestLotAsync(sale.RaftId, new HarvestFields { Lanes = ["3"], Cages = "12" }.For(request.Id()));
            await owner.HarvestLotAsync(sale.RaftId, new HarvestFields { Lanes = ["4"], Cages = "6" }.For(request.Id()));

            var ready = await owner.RequestDetailAsync(request.Id());
            var harvest = ready.GetProperty("harvest");
            Assert.Equal((18, 2, true), (harvest.GetProperty("harvestedCages").GetInt32(), harvest.GetProperty("lots").GetArrayLength(), Flag(ready, "canMarkDone")));
            Assert.Equal([12, 6], harvest.GetProperty("lots").EnumerateArray().Select(l => l.GetProperty("cageCount").GetInt32()));
            Assert.False(Flag(await sale.Manager.Client.RequestDetailAsync(request.Id()), "canMarkDone"));
            Assert.Equal("MSG-GEN-08", await (await sale.Manager.Client.PostHarvestDoneAsync(request.Id(), ready.RowVersion())).ErrorCodeAsync());

            var done = await (await owner.PostHarvestDoneAsync(request.Id(), ready.RowVersion())).ReadAsync(HttpStatusCode.OK);

            Assert.NotEqual(JsonValueKind.Null, done.GetProperty("harvestCompletedAt").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, done.GetProperty("harvest").GetProperty("completedByName").ValueKind);
            Assert.Equal((false, true), (Flag(done, "canMarkDone"), Flag(done, "canUnmarkDone")));
            var mine = await trader.Client.MineAsync(request.Id());
            Assert.NotEqual(JsonValueKind.Null, mine.GetProperty("harvestCompletedAt").ValueKind);
            Assert.False(mine.TryGetProperty("harvest", out _));
            Assert.Contains(await api.AuditRowsAsync(request.Id()), a => a.Action == "PurchaseRequest.HarvestDone");

            Assert.Equal("MSG-GEN-09", await (await owner.PostHarvestDoneAsync(request.Id(), done.RowVersion())).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-09", await (await sale.Manager.Client.PostHarvestAsync(sale.RaftId, new HarvestFields().For(request.Id()))).ErrorCodeAsync());
            var offered = (await sale.Manager.Client.HarvestScreenAsync(sale.RaftId)).GetProperty("orders").EnumerateArray().Select(o => o.Id());
            Assert.DoesNotContain(request.Id(), offered);
        }
    }

    [Fact]
    public async Task The_mark_is_cleared_for_a_raft_still_to_harvest_until_the_request_is_weighed()
    {
        var (api, sale, _, request) = await AcceptedAsync();
        using (api)
        {
            var owner = sale.Owner.Client;
            var harvested = await sale.HarvestedForAsync(request);
            var done = await owner.RequestDetailAsync(request.Id());
            Assert.Equal("MSG-GEN-08", await (await sale.Manager.Client.PostClearHarvestDoneAsync(request.Id(), done.RowVersion())).ErrorCodeAsync());

            var cleared = await (await owner.PostClearHarvestDoneAsync(request.Id(), done.RowVersion())).ReadAsync(HttpStatusCode.OK);
            Assert.Equal(JsonValueKind.Null, cleared.GetProperty("harvestCompletedAt").ValueKind);
            Assert.True(Flag(cleared, "canMarkDone"));
            Assert.Equal("MSG-GEN-09", await (await owner.PostClearHarvestDoneAsync(request.Id(), cleared.RowVersion())).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-09", await (await sale.Manager.Client.PostWeighingAsync(request.Id(), WeighedLot(harvested.LotId, 10m))).ErrorCodeAsync());

            // The forgotten trip, the mark again, and the weighing: the mark then stays.
            await owner.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(5).For(request.Id()));
            Assert.Equal(3, (await owner.MarkHarvestDoneAsync(request.Id())).GetProperty("harvest").GetProperty("lots").GetArrayLength());
            await sale.Manager.Client.WeighAsync(request.Id(), WeighedLot(harvested.LotId, 10m));

            var weighed = await owner.RequestDetailAsync(request.Id());
            Assert.False(Flag(weighed, "canUnmarkDone"));
            Assert.Equal("MSG-GEN-09", await (await owner.PostClearHarvestDoneAsync(request.Id(), weighed.RowVersion())).ErrorCodeAsync());
        }
    }
}
