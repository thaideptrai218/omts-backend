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

        // One cycle grows one kind of oyster: a batch whose seed item sells as another product is refused on Seed lot, and the cycle keeps its product.
        var refusedBatch = await (await client.PutAsJsonAsync($"/api/cultivation-cycles/{started.Id()}", new { nurseryBatchId = batchOfOther.Id(), rowVersion = await api.IssueRowVersionAsync<CultivationCycle>(started.Id()) })).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Seed lot", "The seed grows another oyster than the cycle"), (refusedBatch.Text("code"), refusedBatch.Text("field"), refusedBatch.Text("hint")));
        Assert.Equal(first, await api.QueryAsync(db => db.CultivationCycles.Where(c => c.Id == started.Id()).Select(c => c.ProductId).SingleAsync()));

        // A product sent with the edit is ignored; a batch of the same product is taken (before the first seeding record) and the product stays.
        await NurseryTestKit.EnsureSeedItemProductAsync(api.Services, other.LineId, first);
        var edited = (await (await client.PutAsJsonAsync($"/api/cultivation-cycles/{started.Id()}", new { productId = second, nurseryBatchId = batchOfOther.Id(), rowVersion = await api.IssueRowVersionAsync<CultivationCycle>(started.Id()) })).ReadAsync(HttpStatusCode.OK)).GetProperty("cycle");
        Assert.Equal((batchOfOther.Id(), first), (edited.GetProperty("nurseryBatchId").GetGuid(), edited.GetProperty("productId").GetGuid()));
        Assert.Equal(first, await api.QueryAsync(db => db.CultivationCycles.Where(c => c.Id == started.Id()).Select(c => c.ProductId).SingleAsync()));
    }

    [Fact]
    public async Task A_cycle_takes_the_product_of_its_first_seeding_and_then_takes_only_seed_of_that_product()
    {
        using var api = new AuthApiFactory();
        var setup = await api.CycleAsync();
        var owner = setup.Owner.Client;
        var (product, other) = (await NurseryTestKit.ProductRowAsync(api.Services), await NurseryTestKit.ProductRowAsync(api.Services));
        var lot = await owner.SeedLotAsync(productId: product);
        var sameProduct = await owner.SeedLotAsync(productId: product);
        var anotherProduct = await owner.SeedLotAsync(productId: other);
        var noProduct = await owner.SeedLotAsync();
        Assert.Null(await api.QueryAsync(db => db.CultivationCycles.Where(c => c.Id == setup.CycleId).Select(c => c.ProductId).SingleAsync()));

        // The first seeding gives the cycle the product of its seed.
        await owner.SeedingRecordAsync(setup.CycleId, lot.Session([1]));
        Assert.Equal(product, await api.QueryAsync(db => db.CultivationCycles.Where(c => c.Id == setup.CycleId).Select(c => c.ProductId).SingleAsync()));

        // The seed of another oyster, or of none, is refused on Seed lot; nothing is saved.
        foreach (var refusedLot in new[] { anotherProduct, noProduct })
        {
            var refused = await (await owner.PostSeedingAsync(setup.CycleId, refusedLot.Session([2]))).ReadAsync(HttpStatusCode.BadRequest);
            Assert.Equal(("MSG-GEN-01", "Seed lot", "The seed grows another oyster than the cycle"), (refused.Text("code"), refused.Text("field"), refused.Text("hint")));
        }

        Assert.Equal(1, await api.QueryAsync(db => db.SeedingRecords.CountAsync(r => r.CycleId == setup.CycleId)));

        // Another lot of the same product goes on.
        await owner.SeedingRecordAsync(setup.CycleId, sameProduct.Session([2]));
        Assert.Equal(2, await api.QueryAsync(db => db.SeedingRecords.CountAsync(r => r.CycleId == setup.CycleId)));
    }

    [Fact]
    public async Task A_seed_item_carries_the_product_it_sells_as_and_other_types_ignore_it()
    {
        using var api = new AuthApiFactory();
        var owner = (await api.OwnerAsync()).Client;
        var product = await NurseryTestKit.ProductRowAsync(api.Services);
        var stopped = await NurseryTestKit.ProductRowAsync(api.Services, StatusIds.Usage.Discontinued);

        Assert.Equal(["Product"], await (await owner.PostAsJsonAsync("/api/farming-input-items", new { name = "Giống A", group = "Seed", unit = "con", productId = Guid.NewGuid() })).InvalidFieldsAsync());
        Assert.Equal(["Product"], await (await owner.PostAsJsonAsync("/api/farming-input-items", new { name = "Giống A", group = "Seed", unit = "con", productId = stopped })).InvalidFieldsAsync());
        Assert.Equal(["Product"], await (await owner.PostAsJsonAsync("/api/farming-input-items", new { name = "Giống A", group = "Seed", unit = "con" })).InvalidFieldsAsync());
        var seed = await (await owner.PostAsJsonAsync("/api/farming-input-items", new { name = "Giống B", group = "Seed", unit = "con", productId = product })).ReadAsync(HttpStatusCode.Created);
        var material = await (await owner.PostAsJsonAsync("/api/farming-input-items", new { name = "Phao B", group = "Material", unit = "cái", productId = product })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(product, seed.GetProperty("productId").GetGuid());
        Assert.NotNull(seed.Text("productName"));
        Assert.Equal(JsonValueKind.Null, material.GetProperty("productId").ValueKind);

        // A Seed item always keeps a product: a save without it is refused on Product.
        var cleared = await owner.PutAsJsonAsync($"/api/farming-input-items/{seed.Id()}", new { name = "Giống B", group = "Seed", unit = "con", rowVersion = await api.IssueRowVersionAsync<FarmingInputItem>(seed.Id()) });
        Assert.Equal(["Product"], await cleared.InvalidFieldsAsync());
    }

    [Fact]
    public async Task A_lot_holds_one_line_of_the_product_the_cycle_farms_and_no_other_product()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var product = await NurseryTestKit.ProductRowAsync(api.Services);
        var other = await NurseryTestKit.ProductRowAsync(api.Services);
        await api.UpdateAsync<CultivationCycle>(raft.CycleId, c => c.ProductId = product);

        var refused = await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields().WithProducts((other, 8m, null)))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-09", "HARVEST_PRODUCT_MISMATCH"), (refused.Text("code"), refused.Text("reasonCode")));
        Assert.Equal(0, await api.LotCountAsync(raft.RaftId));

        // A lot holds the oyster of its cycle only: two lines are refused on Product lines, even with the product of the cycle among them.
        var twoLines = await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields().WithProducts((product, 5.5m, null), (other, 2.5m, null)))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Product lines"), (twoLines.Text("code"), twoLines.Text("field")));
        Assert.Equal(0, await api.LotCountAsync(raft.RaftId));

        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields().WithProducts((product, 8m, null)));
        Assert.Equal(8m, lot.Kg("cageCount"));
        Assert.Equal([product], lot.GetProperty("productLines").EnumerateArray().Select(l => l.GetProperty("productId").GetGuid()));
        Assert.Equal(1, await api.LotCountAsync(raft.RaftId));

        // A form that sends no line gets the one line of the cycle product, with its cages.
        var built = await api.HarvestLotOfLanesAsync(owner, raft.RaftId, HarvestFields.OfLanes(2));
        var line = Assert.Single(built.GetProperty("productLines").EnumerateArray());
        Assert.Equal((product, 8m), (line.GetProperty("productId").GetGuid(), line.Kg("cageCount")));
    }
}
