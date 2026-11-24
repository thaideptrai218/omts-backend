using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// PURCHASE-01.4: Copy fills a new receipt, saves nothing and leaves out what is no longer In Use.
/// </summary>
public class PurchaseReceiptCorrectCopyTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Copy_fills_a_new_receipt_without_saving_and_leaves_out_what_is_no_longer_in_use()
    {
        var world = await factory.WorldAsync();
        // A receipt holds one kind: the copied one is a seed receipt, whose lines are all seed items.
        var oldItem = await world.Owner.CreateItemAsync("Seed", "kg");
        var third = await world.Owner.CreateItemAsync("Seed", "kg");
        var (_, foreignRaft) = await factory.ZoneWithRaftAsync();
        var source = await world.CompletedAsync(
            new LineBody(world.Seed, 10, 900, "LOT-9", world.Raft.Id), new LineBody(oldItem, 2, 5_000, RaftId: world.Raft.Id),
            new LineBody(third, 3, 1_000, RaftId: foreignRaft.Id), new LineBody(world.Seed, 1.5m, 333));
        await (await world.Owner.CancelAsync(source.Id(), "Wrong quantities")).ReadAsync(HttpStatusCode.OK);
        await (await world.Owner.PostAsync($"/api/farming-input-items/{oldItem}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        var receipts = await factory.QueryAsync(db => db.PurchaseReceipts.CountAsync());

        // A Zone Manager of the world's zone: the raft of the other zone is not offered, so it is left out too.
        var manager = await factory.ZoneManagerAsync(world.Zone);
        var copy = await (await manager.GetAsync($"/api/purchase-receipts/{source.Id()}/copy")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((source.Id(), source.GetProperty("code").GetString()), (copy.GetProperty("copiedFromId").GetGuid(), copy.GetProperty("copiedFromCode").GetString()));
        var body = copy.GetProperty("receipt");
        Assert.Equal("Seed", body.GetProperty("kind").GetString());
        Assert.Equal((world.SupplierId, source.GetProperty("receiptDate").GetString()), (body.GetProperty("supplierId").GetGuid(), body.GetProperty("receiptDate").GetString()));
        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal((world.Seed, 10m, 900m, "LOT-9", world.Raft.Id), (lines[0].GetProperty("itemId").GetGuid(), lines[0].Money("quantity"), lines[0].Money("unitPrice"),
            lines[0].GetProperty("supplierLotCode").GetString(), lines[0].GetProperty("raftId").GetGuid()));
        // The raft goes with the item left empty (it depends on the group of the item); only the item is listed.
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (lines[1].GetProperty("itemId").ValueKind, lines[1].GetProperty("raftId").ValueKind));
        Assert.Equal(JsonValueKind.Null, lines[2].GetProperty("raftId").ValueKind);
        Assert.Equal(1.5m, lines[3].Money("quantity"));
        Assert.Equal(["Line 2 item", "Line 3 raft"], copy.GetProperty("leftEmpty").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(receipts, await factory.QueryAsync(db => db.PurchaseReceipts.CountAsync()));

        // The prefilled receipt is saved like any other, after filling what was left out.
        var saved = await manager.CreateReceiptAsync(world.Body(
            new LineBody(world.Seed, 10, 900, "LOT-9", world.Raft.Id), new LineBody(third, 2, 5_000, RaftId: world.Raft.Id)) with { Complete = true });
        Assert.NotEqual(source.GetProperty("code").GetString(), saved.GetProperty("code").GetString());

        // A discontinued supplier is left out as well.
        await (await world.Owner.PostAsync($"/api/suppliers/{world.SupplierId}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        var withoutSupplier = await (await world.Owner.GetAsync($"/api/purchase-receipts/{source.Id()}/copy")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, withoutSupplier.GetProperty("receipt").GetProperty("supplierId").ValueKind);
        Assert.Contains("Supplier", withoutSupplier.GetProperty("leftEmpty").EnumerateArray().Select(e => e.GetString()));
        // The Owner may name any raft, so the raft of the other zone stays.
        Assert.Equal(foreignRaft.Id, withoutSupplier.GetProperty("receipt").GetProperty("lines")[2].GetProperty("raftId").GetGuid());
        await (await world.Owner.GetAsync($"/api/purchase-receipts/{Guid.NewGuid()}/copy")).ReadAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Copy_leaves_out_a_discontinued_raft()
    {
        var world = await factory.WorldAsync();
        var source = await world.CompletedAsync(world.MaterialForRaft(1, 1_000));
        await factory.UpdateAsync<Raft>(world.Raft.Id, r => r.StatusId = StatusIds.Usage.Discontinued);

        var copy = await (await world.Owner.GetAsync($"/api/purchase-receipts/{source.Id()}/copy")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(JsonValueKind.Null, copy.GetProperty("receipt").GetProperty("lines")[0].GetProperty("raftId").ValueKind);
        Assert.Equal(["Line 1 raft"], copy.GetProperty("leftEmpty").EnumerateArray().Select(e => e.GetString()));
    }
}
