using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>Decision of 11/11/2026: the Cooperative Owner marks the harvest of an Accepted request done once its lots are in, and clears the mark again.</summary>
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

    private const string NeedsRequestLots = "A harvest lot no longer names a purchase request.";

    private static bool Flag(JsonElement detail, string name) => detail.GetProperty("harvest").GetProperty(name).GetBoolean();

    [Fact(Skip = NeedsRequestLots)]
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
            await sale.Manager.Client.HarvestLotAsync(sale.RaftId, new HarvestFields { Lanes = ["3"], Cages = "12" });
            await owner.HarvestLotAsync(sale.RaftId, new HarvestFields { Lanes = ["4"], Cages = "6" });

            var ready = await owner.RequestDetailAsync(request.Id());
            var harvest = ready.GetProperty("harvest");
            Assert.Equal((18, 2, true), (harvest.GetProperty("harvestedCages").GetInt32(), harvest.GetProperty("lots").GetArrayLength(), Flag(ready, "canMarkDone")));
            Assert.Equal([12, 6], harvest.GetProperty("lots").EnumerateArray().Select(l => l.GetProperty("cageCount").GetInt32()));
            Assert.False(Flag(await sale.Accountant.Client.RequestDetailAsync(request.Id()), "canMarkDone"));
            Assert.Equal("MSG-GEN-08", await (await sale.Accountant.Client.PostHarvestDoneAsync(request.Id(), ready.RowVersion())).ErrorCodeAsync());

            var done = await (await owner.PostHarvestDoneAsync(request.Id(), ready.RowVersion())).ReadAsync(HttpStatusCode.OK);

            Assert.NotEqual(JsonValueKind.Null, done.GetProperty("harvestCompletedAt").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, done.GetProperty("harvest").GetProperty("completedByName").ValueKind);
            Assert.Equal((false, true), (Flag(done, "canMarkDone"), Flag(done, "canUnmarkDone")));
            var mine = await trader.Client.MineAsync(request.Id());
            Assert.NotEqual(JsonValueKind.Null, mine.GetProperty("harvestCompletedAt").ValueKind);
            Assert.False(mine.TryGetProperty("harvest", out _));
            Assert.Contains(await api.AuditRowsAsync(request.Id()), a => a.Action == "PurchaseRequest.HarvestDone");

            Assert.Equal("MSG-GEN-09", await (await owner.PostHarvestDoneAsync(request.Id(), done.RowVersion())).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-09", await (await sale.Manager.Client.PostHarvestAsync(sale.RaftId, new HarvestFields())).ErrorCodeAsync());
            var offered = (await sale.Manager.Client.HarvestScreenAsync(sale.RaftId)).GetProperty("orders").EnumerateArray().Select(o => o.Id());
            Assert.DoesNotContain(request.Id(), offered);
        }
    }

    [Fact(Skip = NeedsRequestLots)]
    public async Task The_mark_is_cleared_for_a_raft_still_to_harvest()
    {
        var (api, sale, _, request) = await AcceptedAsync();
        using (api)
        {
            var owner = sale.Owner.Client;
            await HarvestedForAsync(sale, request);
            var done = await owner.RequestDetailAsync(request.Id());
            Assert.Equal("MSG-GEN-08", await (await sale.Accountant.Client.PostClearHarvestDoneAsync(request.Id(), done.RowVersion())).ErrorCodeAsync());

            var cleared = await (await owner.PostClearHarvestDoneAsync(request.Id(), done.RowVersion())).ReadAsync(HttpStatusCode.OK);
            Assert.Equal(JsonValueKind.Null, cleared.GetProperty("harvestCompletedAt").ValueKind);
            Assert.True(Flag(cleared, "canMarkDone"));
            Assert.Equal("MSG-GEN-09", await (await owner.PostClearHarvestDoneAsync(request.Id(), cleared.RowVersion())).ErrorCodeAsync());

            // The forgotten trip, then the mark again.
            await owner.HarvestLotAsync(sale.RaftId, (HarvestFields.OfLanes(5) with { Cages = "4" }));
            Assert.Equal(3, (await owner.MarkHarvestDoneAsync(request.Id())).GetProperty("harvest").GetProperty("lots").GetArrayLength());
            Assert.True(Flag(await owner.RequestDetailAsync(request.Id()), "canUnmarkDone"));
        }
    }

    /// <summary>
    /// Records lots on the sale's raft for the request, one per number of cages (two of eight when none are given), and marks its harvest done. Answers the setup with the first lots
    /// in place of the sale's own.
    /// </summary>
    private static async Task<SaleSetup> HarvestedForAsync(SaleSetup sale, JsonElement request, params int[] cagesOfLots)
    {
        int[] counts = cagesOfLots.Length == 0 ? [8, 8] : cagesOfLots;
        var lots = new List<Guid>();
        // The lots of a request never exceed its accepted cages (13/11/2026): a small request is given room while the lots are recorded, then back its own cages.
        var accepted = await SetAcceptedCagesAsync(sale.Owner.Client, request.Id(), atLeast: counts.Sum());
        foreach (var cages in counts)
        {
            lots.Add((await sale.Owner.Client.HarvestLotAsync(sale.RaftId, new HarvestFields { Lanes = ["3"], Cages = cages.ToString() })).Id());
        }

        await SetAcceptedCagesAsync(sale.Owner.Client, request.Id(), exactly: accepted);
        await sale.Owner.Client.MarkHarvestDoneAsync(request.Id());
        return sale with { LotId = lots[0], OtherLotId = lots.Count > 1 ? lots[1] : sale.OtherLotId };
    }

    /// <summary>Writes the accepted cages of a request straight to the database: at least <paramref name="atLeast"/>, or exactly <paramref name="exactly"/>. Answers what it was before.</summary>
    private static async Task<int?> SetAcceptedCagesAsync(HttpClient client, Guid requestId, int? atLeast = null, int? exactly = null)
    {
        using var scope = TestHosts.ServicesOf(client).CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var row = await db.PurchaseRequests.SingleAsync(r => r.Id == requestId);
        var before = row.AcceptedCages;
        row.AcceptedCages = atLeast is { } least ? Math.Max(before ?? 0, least) : exactly;
        await db.SaveChangesAsync();
        return before;
    }
}
