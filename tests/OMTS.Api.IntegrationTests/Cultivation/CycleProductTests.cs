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
using OMTS.Domain.Entities.Purchasing;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// The product a cycle farms (owner decision of 13/11/2026): derived from the seed item of its nursery batch (one choice only), followed when the batch changes before the first seeding record, and the one a harvest lot's purchase request must match.
/// </summary>
public sealed class CycleProductTests
{
    [Fact]
    public async Task A_cycle_farms_the_product_of_the_seed_item_of_its_batch_and_follows_it_when_the_batch_changes()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "10000", "9500");
        var other = await client.SeedLotAsync(10_000m);
        var batchOfOther = await client.ReadyNurseryAsync(api, world.ZoneId, other, "5000", "4800");
        var first = await NurseryTestKit.EnsureSeedItemProductAsync(api.Services, world.Lot.LineId);
        var second = await NurseryTestKit.EnsureSeedItemProductAsync(api.Services, other.LineId);
        Assert.NotEqual(first, second);

        // The batch is chosen, the product follows from its seed item; an item that sells as nothing yet is refused on the batch.
        await NurseryTestKit.ClearSeedItemProductAsync(api.Services, world.Lot.LineId);
        var refused = await (await client.PostNurseryCycleAsync(world.Raft1, batch.Id())).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "NurseryBatchId", "SEED_ITEM_WITHOUT_PRODUCT"), (refused.Text("code"), refused.Text("field"), refused.Text("reasonCode")));
        Assert.Contains("chọn loại hàu bán ra", refused.Text("hint"));
        Assert.False(await api.QueryAsync(db => db.CultivationCycles.AnyAsync(c => c.RaftId == world.Raft1)));

        await NurseryTestKit.EnsureSeedItemProductAsync(api.Services, world.Lot.LineId, first);
        var started = (await (await client.PostNurseryCycleAsync(world.Raft1, batch.Id())).ReadAsync(HttpStatusCode.Created)).GetProperty("cycle");
        var (firstName, itemName) = await api.QueryAsync(async db => (
            await db.Products.Where(p => p.Id == first).Select(p => p.Name).SingleAsync(), await db.FarmingInputItems.Where(i => i.Id == world.Lot.ItemId).Select(i => i.Name).SingleAsync()));
        Assert.Equal((first, firstName, itemName), (started.GetProperty("productId").GetGuid(), started.Text("productName"), started.Text("seedItemName")));

        // A product sent with the edit is ignored; changing the batch (before the first seeding record) moves the product to the one of the new batch's seed item.
        var edited = (await (await client.PutAsJsonAsync($"/api/cultivation-cycles/{started.Id()}", new { productId = first, nurseryBatchId = batchOfOther.Id(), rowVersion = await api.IssueRowVersionAsync<CultivationCycle>(started.Id()) })).ReadAsync(HttpStatusCode.OK)).GetProperty("cycle");
        Assert.Equal(second, edited.GetProperty("productId").GetGuid());
        Assert.Equal(second, await api.QueryAsync(db => db.CultivationCycles.Where(c => c.Id == started.Id()).Select(c => c.ProductId).SingleAsync()));
    }

    [Fact]
    public async Task A_seed_item_carries_the_product_it_sells_as_and_other_types_ignore_it()
    {
        using var api = new AuthApiFactory();
        var owner = (await api.OwnerAsync()).Client;
        var product = await NurseryTestKit.ProductRowAsync(api.Services);
        var stopped = await NurseryTestKit.ProductRowAsync(api.Services, StatusIds.Usage.Discontinued);

        Assert.Equal(["ProductId"], await (await owner.PostAsJsonAsync("/api/farming-input-items", new { name = "Giống A", group = "Seed", unit = "con", productId = Guid.NewGuid() })).InvalidFieldsAsync());
        Assert.Equal(["ProductId"], await (await owner.PostAsJsonAsync("/api/farming-input-items", new { name = "Giống A", group = "Seed", unit = "con", productId = stopped })).InvalidFieldsAsync());
        var seed = await (await owner.PostAsJsonAsync("/api/farming-input-items", new { name = "Giống B", group = "Seed", unit = "con", productId = product })).ReadAsync(HttpStatusCode.Created);
        var material = await (await owner.PostAsJsonAsync("/api/farming-input-items", new { name = "Phao B", group = "Material", unit = "cái", productId = product })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(product, seed.GetProperty("productId").GetGuid());
        Assert.NotNull(seed.Text("productName"));
        Assert.Equal(JsonValueKind.Null, material.GetProperty("productId").ValueKind);

        // Like every field of the form, a save without the product clears it.
        var cleared = await (await owner.PutAsJsonAsync($"/api/farming-input-items/{seed.Id()}", new { name = "Giống B", group = "Seed", unit = "con", rowVersion = await api.IssueRowVersionAsync<FarmingInputItem>(seed.Id()) })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("productId").ValueKind);
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
