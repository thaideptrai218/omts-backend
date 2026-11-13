using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// The product a cycle farms (owner decision of 13/11/2026): required to start a cycle from a batch, changeable only before the first seeding record, and the one a harvest lot's purchase request must match.
/// </summary>
public sealed class CycleProductTests
{
    [Fact]
    public async Task A_cycle_needs_a_product_in_use_and_changes_it_only_before_its_first_seeding_record()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "10000", "9500");

        Assert.Equal(["ProductId"], await (await client.PostNurseryCycleAsync(world.Raft1, batch.Id(), noProduct: true)).InvalidFieldsAsync());
        Assert.Equal(["ProductId"], await (await client.PostNurseryCycleAsync(world.Raft1, batch.Id(), productId: Guid.NewGuid())).InvalidFieldsAsync());
        var stopped = await NurseryTestKit.ProductRowAsync(api.Services, StatusIds.Usage.Discontinued);
        Assert.Equal(["ProductId"], await (await client.PostNurseryCycleAsync(world.Raft1, batch.Id(), productId: stopped)).InvalidFieldsAsync());
        Assert.False(await api.QueryAsync(db => db.CultivationCycles.AnyAsync(c => c.RaftId == world.Raft1)));

        var first = await NurseryTestKit.ProductRowAsync(api.Services);
        var second = await NurseryTestKit.ProductRowAsync(api.Services);
        var cycle = await (await client.PostNurseryCycleAsync(world.Raft1, batch.Id(), productId: first)).ReadAsync(HttpStatusCode.Created);
        var started = cycle.GetProperty("cycle");
        var firstName = await api.QueryAsync(db => db.Products.Where(p => p.Id == first).Select(p => p.Name).SingleAsync());
        Assert.Equal((first, firstName, true), (started.GetProperty("productId").GetGuid(), started.Text("productName"), started.Flag("canChangeProduct")));
        Assert.Equal(first, (await client.CycleViewAsync(started.Id())).GetProperty("productId").GetGuid());

        // Before the first seeding record the product can change; a product In Use is needed.
        var changed = (await (await client.PutAsJsonAsync($"/api/cultivation-cycles/{started.Id()}", new { productId = second, rowVersion = await api.IssueRowVersionAsync<CultivationCycle>(started.Id()) })).ReadAsync(HttpStatusCode.OK)).GetProperty("cycle");
        Assert.Equal(second, changed.GetProperty("productId").GetGuid());
        Assert.Equal(["ProductId"], await (await client.PutAsJsonAsync($"/api/cultivation-cycles/{started.Id()}", new { productId = stopped, rowVersion = await api.IssueRowVersionAsync<CultivationCycle>(started.Id()) })).InvalidFieldsAsync());

        // The first seeding record locks it; the same product sent again is no change.
        await client.SeedingRecordAsync(started.Id(), NurseryTestKit.NurserySession([1], 500m));
        var locked = await (await client.PutAsJsonAsync($"/api/cultivation-cycles/{started.Id()}", new { productId = first, rowVersion = await api.IssueRowVersionAsync<CultivationCycle>(started.Id()) })).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("CYCLE_SEED_LOCKED", "MSG-GEN-09", "ProductId"), (locked.Text("reasonCode"), locked.Text("code"), locked.Text("field")));
        var same = (await (await client.PutAsJsonAsync($"/api/cultivation-cycles/{started.Id()}", new { productId = second, targetYieldKg = 2100, rowVersion = await api.IssueRowVersionAsync<CultivationCycle>(started.Id()) })).ReadAsync(HttpStatusCode.OK)).GetProperty("cycle");
        Assert.Equal((second, false), (same.GetProperty("productId").GetGuid(), same.Flag("canChangeProduct")));
    }

    [Fact]
    public async Task A_lot_is_recorded_only_for_a_request_of_the_product_the_cycle_farms_and_the_screen_says_which_match()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var other = await HarvestOrderKit.OrderAsync(api.Services);
        var matching = await HarvestOrderKit.OrderAsync(api.Services);

        // A cycle without a product (started before) is not checked: any open request takes a lot.
        var screenBefore = await owner.HarvestScreenAsync(raft.RaftId);
        Assert.Equal(JsonValueKind.Null, screenBefore.GetProperty("cycleProductId").ValueKind);
        Assert.All(screenBefore.GetProperty("orders").EnumerateArray(), o => Assert.True(o.Flag("matchesCycleProduct")));

        var product = await NurseryTestKit.ProductRowAsync(api.Services);
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
            var cycle = await db.CultivationCycles.SingleAsync(c => c.Id == raft.CycleId);
            cycle.ProductId = product;
            var request = await db.PurchaseRequests.Include(r => r.Product).SingleAsync(r => r.Id == matching);
            request.ProductId = product;
            request.ProductName = (await db.Products.SingleAsync(p => p.Id == product)).Name;
            await db.SaveChangesAsync();
        }

        var productName = await api.QueryAsync(db => db.Products.Where(p => p.Id == product).Select(p => p.Name).SingleAsync());
        var screen = await owner.HarvestScreenAsync(raft.RaftId);
        Assert.Equal((product, productName), (screen.GetProperty("cycleProductId").GetGuid(), screen.Text("cycleProductName")));
        var orders = screen.GetProperty("orders").EnumerateArray().ToDictionary(o => o.Id());
        Assert.Equal((true, productName), (orders[matching].Flag("matchesCycleProduct"), orders[matching].Text("productName")));
        Assert.False(orders[other].Flag("matchesCycleProduct"));
        Assert.Equal(product, orders[matching].GetProperty("productId").GetGuid());

        var refused = await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields().For(other))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-09", "HARVEST_PRODUCT_MISMATCH"), (refused.Text("code"), refused.Text("reasonCode")));
        Assert.Contains(productName, refused.Text("detail"));
        Assert.Contains("Chọn đơn mua khớp loại hàu đang nuôi", refused.Text("detail"));
        Assert.Equal(0, await api.LotCountAsync(raft.RaftId));

        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields().For(matching));
        Assert.Equal(matching, lot.GetProperty("purchaseRequestId").GetGuid());
        Assert.Equal(1, await api.LotCountAsync(raft.RaftId));
    }
}
