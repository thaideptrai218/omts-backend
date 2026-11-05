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
/// PURCHASE-06.2: the Owner corrects a receipt that can no longer be cancelled (quantity not below seeded + returned,
/// MSG-PURCHASE-10), with the values before and after in the audit log; PURCHASE-01.4: Copy fills a new receipt, saves
/// nothing and leaves out what is no longer In Use.
/// </summary>
public class PurchaseReceiptCorrectCopyTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static Task<HttpResponseMessage> CorrectAsync(HttpClient client, Guid receiptId, string? reason, params object[] lines) =>
        client.PostAsJsonAsync($"/api/purchase-receipts/{receiptId}/correct", new { reason, lines });

    [Fact]
    public async Task The_owner_corrects_a_seeded_receipt_never_below_seeded_plus_returned_and_the_audit_keeps_before_and_after()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Seed, 100, 9_000, "LOT-1"), new LineBody(world.Other, 4, 25_000));
        var (seedLine, otherLine) = (receipt.LineOf(world.Seed), receipt.LineOf(world.Other));
        await factory.SeedAsync(world.Raft.Id, seedLine, 30);
        await (await world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody { DebtReductionAmount = 90_000, Lines = [new ReturnLineBody(seedLine, 10)] }))
            .ReadAsync(HttpStatusCode.Created);

        var below = await (await CorrectAsync(world.Owner, receipt.Id(), "Counted again", new { lineId = seedLine, quantity = 39.5m }))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-10");
        var itemName = await factory.QueryAsync(db => db.FarmingInputItems.Where(i => i.Id == world.Seed).Select(i => i.Name).SingleAsync());
        Assert.Equal($"{itemName}: the quantity cannot be less than 40 kg (30 seeded, 10 returned).", below.GetProperty("detail").GetString());
        Assert.Equal(("Line 1", 40m, 30m, 10m), (below.GetProperty("field").GetString(), below.Money("minQuantity"), below.Money("seededQuantity"), below.Money("returnedQuantity")));
        Assert.Equal("Reason", await (await CorrectAsync(world.Owner, receipt.Id(), " ", new { lineId = seedLine, quantity = 40 })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Lines", await (await CorrectAsync(world.Owner, receipt.Id(), "x", new { lineId = Guid.NewGuid(), quantity = 40 })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Line 2 supplier lot code", await (await CorrectAsync(world.Owner, receipt.Id(), "x", new { lineId = otherLine, supplierLotCode = "L" }))
            .FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Line 2", await (await CorrectAsync(world.Owner, receipt.Id(), "x", new { lineId = otherLine, unitPrice = 1.5m })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var corrected = await (await CorrectAsync(world.Owner, receipt.Id(), "Supplier invoice checked",
            new { lineId = seedLine, quantity = 40, unitPrice = 8_500, supplierLotCode = "LOT-2" }, new { lineId = otherLine, unitPrice = 24_000 }))
            .ReadAsync(HttpStatusCode.OK);

        var seed = corrected.LineViewOf(world.Seed);
        Assert.Equal((40m, 8_500m, 340_000m, "LOT-2"), (seed.Money("quantity"), seed.Money("unitPrice"), seed.Money("lineAmount"), seed.GetProperty("supplierLotCode").GetString()));
        Assert.Equal(96_000m, corrected.LineViewOf(world.Other).Money("lineAmount"));
        Assert.Equal(436_000m, corrected.Money("total"));
        Assert.Equal(receipt.SeedLotCodeOf(world.Seed), corrected.SeedLotCodeOf(world.Seed));
        // The supplier debt follows the new amounts (BR-PURCHASE-04).
        Assert.Equal(436_000m - 90_000m, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}/debt")).GetProperty("totals").Money("outstanding"));
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == receipt.Id() && e.Action == "PurchaseReceipt.Corrected"));
        Assert.Equal("Supplier invoice checked", audit.Reason);
        Assert.Contains("9000", audit.Before);
        Assert.Contains("8500", audit.After);
        Assert.Contains("LOT-1", audit.Before);
        // A correction sends no notification (BR-PURCHASE-05): only the completion one.
        Assert.Equal(
            await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.RelatedRecordId == receipt.Id() && n.Type == NotificationTypes.PurchaseReceiptCompleted)),
            await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.RelatedRecordId == receipt.Id())));
    }

    [Fact]
    public async Task Only_the_owner_corrects_and_only_a_completed_receipt_that_can_no_longer_be_cancelled()
    {
        var world = await factory.WorldAsync();
        var cancellable = await world.CompletedAsync(new LineBody(world.Other, 1, 10_000));
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)));
        var line = new { lineId = cancellable.LineOf(world.Other), unitPrice = 9_000 };

        var hint = await (await CorrectAsync(world.Owner, cancellable.Id(), "Price", line)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Contains("cancel it and copy it", hint.GetProperty("hint").GetString());
        await (await CorrectAsync(world.Owner, draft.Id(), "Price", new { lineId = draft.LineOf(world.Other), unitPrice = 9_000 })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        var seeded = await world.CompletedAsync(new LineBody(world.Seed, 10, 9_000));
        await factory.SeedAsync(world.Raft.Id, seeded.LineOf(world.Seed), 2);
        var manager = await factory.ZoneManagerAsync(world.Zone);
        await (await CorrectAsync(manager, seeded.Id(), "Price", new { lineId = seeded.LineOf(world.Seed), unitPrice = 8_000 })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await CorrectAsync(world.Owner, seeded.Id(), "Price", new { lineId = seeded.LineOf(world.Seed), unitPrice = 8_000 })).ReadAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Copy_fills_a_new_receipt_without_saving_and_leaves_out_what_is_no_longer_in_use()
    {
        var world = await factory.WorldAsync();
        var oldItem = await world.Owner.CreateItemAsync("Material", "piece");
        var (_, foreignRaft) = await factory.ZoneWithRaftAsync();
        var source = await world.CompletedAsync(
            new LineBody(world.Seed, 10, 900, "LOT-9", world.Raft.Id), new LineBody(oldItem, 2, 5_000, RaftId: world.Raft.Id),
            new LineBody(world.Material, 3, 1_000, RaftId: foreignRaft.Id), new LineBody(world.Other, 1.5m, 333));
        await (await world.Owner.CancelAsync(source.Id(), "Wrong quantities")).ReadAsync(HttpStatusCode.OK);
        await (await world.Owner.PostAsync($"/api/farming-input-items/{oldItem}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        var receipts = await factory.QueryAsync(db => db.PurchaseReceipts.CountAsync());

        // A Zone Manager of the world's zone: the raft of the other zone is not offered, so it is left out too.
        var manager = await factory.ZoneManagerAsync(world.Zone);
        var copy = await (await manager.GetAsync($"/api/purchase-receipts/{source.Id()}/copy")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((source.Id(), source.GetProperty("code").GetString()), (copy.GetProperty("copiedFromId").GetGuid(), copy.GetProperty("copiedFromCode").GetString()));
        var body = copy.GetProperty("receipt");
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
            new LineBody(world.Seed, 10, 900, "LOT-9", world.Raft.Id), new LineBody(world.Material, 2, 5_000, RaftId: world.Raft.Id)) with { Complete = true });
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
