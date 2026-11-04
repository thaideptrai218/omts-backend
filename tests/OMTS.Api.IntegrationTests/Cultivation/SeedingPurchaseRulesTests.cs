using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;

namespace OMTS.Api.IntegrationTests.Cultivation;

using OMTS.Api.IntegrationTests.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

/// <summary>GBR-04, CYCLE-02 POST-1: a real session blocks the cancel of its receipt and lowers the limit of its returns; correcting or removing it gives that back.</summary>
public class SeedingPurchaseRulesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static Task<JsonElement> ReceiptAsync(HttpClient client, Guid receiptId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{receiptId}");

    [Fact]
    public async Task A_seeded_receipt_cannot_be_cancelled_until_its_sessions_are_removed()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session());

        var refused = await (await owner.CancelAsync(lot.ReceiptId, "Wrong")).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-09");
        Assert.Equal(("Seeded", lot.Code), (refused.Text("cause"), refused.Text("causeCode")));
        var preview = await owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{lot.ReceiptId}/cancel-preview");
        Assert.False(preview.Flag("canCancel"));

        await (await owner.RemoveSeedingAsync(record.Id())).ReadAsync(HttpStatusCode.NoContent);

        await (await owner.CancelAsync(lot.ReceiptId, "Wrong")).ReadAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(lot.LineId, (await owner.SeedLotsAsync(setup.RaftId)).GetProperty("lots").EnumerateArray().Select(l => l.Id()));
    }

    [Fact]
    public async Task A_correction_to_another_lot_moves_the_block_and_the_seeded_quantity()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var (first, second) = (await owner.SeedLotAsync(), await owner.SeedLotAsync());
        var record = await owner.SeedingRecordAsync(setup.CycleId, first.Session(quantity: 30m));

        await (await owner.PutSeedingAsync(record.Id(), record.AsCorrection() with { SeedLotId = second.LineId, Quantity = 40m })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(40m, (await ReceiptAsync(owner, second.ReceiptId)).LineViewOf(second.ItemId).Money("seeded"));
        Assert.Equal(0m, (await ReceiptAsync(owner, first.ReceiptId)).LineViewOf(first.ItemId).Money("seeded"));
        await (await owner.CancelAsync(first.ReceiptId, "Free again")).ReadAsync(HttpStatusCode.OK);
        await (await owner.CancelAsync(second.ReceiptId, "Blocked")).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-09");
    }

    [Fact]
    public async Task Returns_are_limited_to_received_minus_seeded_minus_returned_and_a_removal_gives_it_back()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(quantity: 100m);
        var first = await owner.SeedingRecordAsync(setup.CycleId, lot.Session([1], quantity: 30m));
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([2], quantity: 20m));

        var line = (await ReceiptAsync(owner, lot.ReceiptId)).LineViewOf(lot.ItemId);
        Assert.Equal((50m, 50m), (line.Money("seeded"), line.Money("returnable")));

        var refused = await (await owner.PostReturnAsync(lot.ReceiptId, new ReturnBody { Lines = [new ReturnLineBody(lot.LineId, 50.01m)] })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-03");
        Assert.Equal((50m, 50m), (refused.Money("maxQuantity"), refused.Money("seeded")));

        await owner.ReturnLotAsync(lot, 50m);
        Assert.Equal(0m, (await ReceiptAsync(owner, lot.ReceiptId)).LineViewOf(lot.ItemId).Money("returnable"));

        await (await owner.RemoveSeedingAsync(first.Id())).ReadAsync(HttpStatusCode.NoContent);
        var freed = (await ReceiptAsync(owner, lot.ReceiptId)).LineViewOf(lot.ItemId);
        Assert.Equal((20m, 30m), (freed.Money("seeded"), freed.Money("returnable")));
    }

    // GBR-04: seeding passing the lot leaves nothing to return, never a negative limit, and the Owner's price correction still works.
    [Fact]
    public async Task Seeding_more_than_received_leaves_nothing_to_return_and_the_owner_can_still_correct_the_price()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(quantity: 10m);
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session(quantity: 15m));

        var line = (await ReceiptAsync(owner, lot.ReceiptId)).LineViewOf(lot.ItemId);
        Assert.Equal((15m, 0m), (line.Money("seeded"), line.Money("returnable")));
        await (await owner.PostReturnAsync(lot.ReceiptId, new ReturnBody { Lines = [new ReturnLineBody(lot.LineId, 1m)] })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-03");

        await (await owner.CorrectAsync(lot.ReceiptId, "Invoice price", new { lineId = lot.LineId, unitPrice = 900 })).ReadAsync(HttpStatusCode.OK);
        var below = await (await owner.CorrectAsync(lot.ReceiptId, "Counted again", new { lineId = lot.LineId, quantity = 14 })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-10");
        Assert.Equal(15m, below.Money("minQuantity"));
    }
}
