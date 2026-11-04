using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;

namespace OMTS.Api.IntegrationTests.Cultivation;

using OMTS.Api.IntegrationTests.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

/// <summary>CYCLE-02.0 and BR-CYCLE-02: what a session holds, its date against the cycle, the seed lot and today, its quantity and its lanes.</summary>
public class SeedingRecordTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static async Task AssertInvalidAsync(HttpResponseMessage answer, params string[] fields) =>
        Assert.Equal(fields, await answer.InvalidFieldsAsync());

    [Fact]
    public async Task A_session_holds_its_date_lot_lanes_and_quantity_the_lanes_show_growing_and_nobody_is_told()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var lot = await setup.Owner.Client.SeedLotAsync(raftId: setup.RaftId);
        var noticesBefore = await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.RelatedRecordId == setup.CycleId));

        var answer = await manager.Client.RecordSeedingAsync(setup.CycleId, lot.Session([2, 1], Today.AddDays(-3), 12.5m));

        Assert.Equal(("Seeding session saved.", "MSG-GEN-05", false), (answer.Text("message"), answer.Text("messageCode"), answer.Flag("offerMarkComplete")));
        var record = answer.GetProperty("record");
        Assert.Equal((setup.CycleId, setup.RaftId, Today.AddDays(-3), 12.5m), (record.GetProperty("cycleId").GetGuid(), record.GetProperty("raftId").GetGuid(), record.Date("seedingDate"), record.GetProperty("quantity").GetDecimal()));
        Assert.Equal([1, 2], record.Lanes());
        Assert.Equal(manager.Account.Id, record.GetProperty("recordedByAccountId").GetGuid());
        Assert.True(record.Flag("canCorrect"));

        var seedLot = record.GetProperty("seedLot");
        Assert.Equal((lot.LineId, lot.Code, lot.ReceiptId, lot.SupplierId, Today.AddDays(-10)), (seedLot.Id(), seedLot.Text("code"), seedLot.GetProperty("receiptId").GetGuid(), seedLot.GetProperty("supplierId").GetGuid(), seedLot.Date("receivedDate")));
        Assert.False(seedLot.TryGetProperty("unitPrice", out _));

        var list = await manager.Client.SeedingListAsync(setup.CycleId);
        Assert.Equal(("Growing", "Growing", "NotSeeded"), (list.StateOf(1), list.StateOf(2), list.StateOf(3)));
        Assert.Equal((5, 2), (list.GetProperty("laneCount").GetInt32(), list.GetProperty("seededCount").GetInt32()));
        Assert.Equal([3, 4, 5], list.Numbers("notSeededLanes"));
        Assert.Equal(noticesBefore, await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.RelatedRecordId == setup.CycleId)));

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "SeedingRecord.Created" && e.RecordId == record.Id()));
        Assert.Equal((nameof(Domain.Entities.Cultivation.SeedingRecord), record.Text("raftCode"), setup.ZoneId), (audit.RecordType, audit.RecordCode, audit.ZoneId));
    }

    [Fact]
    public async Task The_date_may_be_the_start_of_the_cycle_the_day_the_lot_was_received_or_today_and_nothing_outside()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var start = Today.AddDays(-10);
        var lateLot = await owner.SeedLotAsync(received: Today.AddDays(-4));
        var oldLot = await owner.SeedLotAsync(received: Today.AddDays(-30));

        // Before the start of the cycle, with a lot received long before it.
        await AssertInvalidAsync(await owner.PostSeedingAsync(setup.CycleId, oldLot.Session([1], start.AddDays(-1))), "Seeding date");
        // After the start of the cycle but before the lot was received.
        await AssertInvalidAsync(await owner.PostSeedingAsync(setup.CycleId, lateLot.Session([1], Today.AddDays(-5))), "Seeding date");
        await AssertInvalidAsync(await owner.PostSeedingAsync(setup.CycleId, oldLot.Session([1], Today.AddDays(1))), "Seeding date");
        await AssertInvalidAsync(await owner.PostSeedingAsync(setup.CycleId, oldLot.Session([1]) with { SeedingDate = null }), "Seeding date");

        Assert.Equal(start, (await owner.SeedingRecordAsync(setup.CycleId, oldLot.Session([1], start))).Date("seedingDate"));
        Assert.Equal(Today.AddDays(-4), (await owner.SeedingRecordAsync(setup.CycleId, lateLot.Session([2], Today.AddDays(-4)))).Date("seedingDate"));
        Assert.Equal(Today, (await owner.SeedingRecordAsync(setup.CycleId, oldLot.Session([3], Today))).Date("seedingDate"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.234")]
    [InlineData("1000000000.01")]
    public async Task A_quantity_is_above_0_with_at_most_2_decimals_as_on_a_receipt_line(string? quantity)
    {
        var setup = await factory.CycleAsync();
        var lot = await setup.Owner.Client.SeedLotAsync();
        var body = lot.Session() with { Quantity = quantity is null ? null : decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture) };

        await AssertInvalidAsync(await setup.Owner.Client.PostSeedingAsync(setup.CycleId, body), "Quantity");

        Assert.Equal(0, await factory.QueryAsync(db => db.SeedingRecords.CountAsync(r => r.CycleId == setup.CycleId)));
    }

    // GBR-04: seeding does not compare the quantity with what is left in the lot; the quantity seeded is shown only.
    [Fact]
    public async Task A_session_may_pass_what_is_left_in_the_lot()
    {
        var setup = await factory.CycleAsync();
        var lot = await setup.Owner.Client.SeedLotAsync(quantity: 10m);

        await setup.Owner.Client.RecordSeedingAsync(setup.CycleId, lot.Session([1], quantity: 0.01m));
        await setup.Owner.Client.RecordSeedingAsync(setup.CycleId, lot.Session([2], quantity: 1_000_000_000m));

        var offered = (await setup.Owner.Client.SeedLotsAsync(setup.RaftId)).GetProperty("lots").EnumerateArray().Single(l => l.Id() == lot.LineId);
        Assert.Equal((10m, 1_000_000_000.01m), (offered.GetProperty("quantityReceived").GetDecimal(), offered.GetProperty("quantitySeeded").GetDecimal()));
    }

    [Fact]
    public async Task The_lanes_are_at_least_one_of_this_raft_in_use_and_each_given_once()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();

        foreach (var lanes in new List<int>?[] { null, [], [1, 1], [6], [0], [-1] })
        {
            await AssertInvalidAsync(await owner.PostSeedingAsync(setup.CycleId, lot.Session() with { LaneNumbers = lanes }), "Lanes");
        }

        // A Removed lane: lanes are removed only with no Open cycle, and the demo of an older database can hold one (BR-CYCLE-02).
        var raftLane = await factory.QueryAsync(db => db.Lanes.SingleAsync(l => l.RaftId == setup.RaftId && l.LaneNumber == 5));
        await factory.UpdateAsync<Domain.Entities.Farming.Lane>(raftLane.Id, l => l.StatusId = Domain.Constants.StatusIds.Lane.Removed);
        await AssertInvalidAsync(await owner.PostSeedingAsync(setup.CycleId, lot.Session([5])), "Lanes");
        var list = await owner.SeedingListAsync(setup.CycleId);
        Assert.Equal(4, list.GetProperty("laneCount").GetInt32());
        Assert.DoesNotContain(5, list.GetProperty("lanes").EnumerateArray().Select(l => l.GetProperty("laneNumber").GetInt32()));
    }

    [Fact]
    public async Task Every_invalid_field_is_listed_and_nothing_is_saved()
    {
        var setup = await factory.CycleAsync();

        var answer = await setup.Owner.Client.PostSeedingAsync(setup.CycleId, new SeedingBody(null, null, null, null));

        await AssertInvalidAsync(answer, "Seeding date", "Seed lot", "Quantity", "Lanes");
        Assert.Equal(0, await factory.QueryAsync(db => db.SeedingRecords.CountAsync(r => r.CycleId == setup.CycleId)));
    }

    // GBR-04: a seed lot is a Seed line of a Completed, not Cancelled receipt whose whole quantity has not been returned.
    [Fact]
    public async Task Only_a_seed_line_of_a_completed_receipt_not_returned_in_whole_can_be_chosen()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;

        await AssertInvalidAsync(await owner.PostSeedingAsync(setup.CycleId, new SeedingBody(Today, Guid.NewGuid(), [1], 1m)), "Seed lot");

        var draft = await owner.SeedLotAsync(complete: false);
        await AssertInvalidAsync(await owner.PostSeedingAsync(setup.CycleId, draft.Session([1])), "Seed lot");

        var cancelled = await owner.SeedLotAsync();
        await (await owner.CancelAsync(cancelled.ReceiptId, "Wrong supplier")).ReadAsync(HttpStatusCode.OK);
        await AssertInvalidAsync(await owner.PostSeedingAsync(setup.CycleId, cancelled.Session([1])), "Seed lot");

        var material = await owner.CreateItemAsync("Material", "piece");
        var materials = await owner.CreateReceiptAsync(new ReceiptBody { SupplierId = await owner.CreateSupplierAsync(), Lines = [new LineBody(material, 1m, 1m)], Complete = true });
        await AssertInvalidAsync(await owner.PostSeedingAsync(setup.CycleId, new SeedingBody(Today, materials.LineOf(material), [1], 1m)), "Seed lot");

        var returned = await owner.SeedLotAsync(quantity: 10m);
        await owner.ReturnLotAsync(returned, 4m);
        await owner.RecordSeedingAsync(setup.CycleId, returned.Session([1]));

        var whole = await owner.SeedLotAsync(quantity: 10m);
        await owner.ReturnLotAsync(whole, 10m);
        await AssertInvalidAsync(await owner.PostSeedingAsync(setup.CycleId, whole.Session([2])), "Seed lot");
    }

    // GBR-01 does not cite CYCLE-02: the seed of a lot is already bought, so a later discontinue of its item or supplier does not stop it being hung.
    [Fact]
    public async Task A_lot_of_a_discontinued_item_stays_a_seed_lot_and_a_cancelled_return_gives_a_lot_back()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        await factory.UpdateAsync<Domain.Entities.Purchasing.FarmingInputItem>(lot.ItemId, i => i.StatusId = Domain.Constants.StatusIds.Usage.Discontinued);

        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([1]));

        var whole = await owner.SeedLotAsync(quantity: 5m);
        var receipt = await owner.ReturnLotAsync(whole, 5m);
        Assert.DoesNotContain(whole.LineId, (await owner.SeedLotsAsync(setup.RaftId)).GetProperty("lots").EnumerateArray().Select(l => l.Id()));
        await (await owner.CancelReturnAsync(receipt.GetProperty("returns")[0].Id(), "Recorded by mistake")).ReadAsync(HttpStatusCode.OK);

        await owner.RecordSeedingAsync(setup.CycleId, whole.Session([2]));
    }

    [Fact]
    public async Task An_unknown_cycle_is_404()
    {
        var setup = await factory.CycleAsync();
        var lot = await setup.Owner.Client.SeedLotAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await setup.Owner.Client.PostSeedingAsync(Guid.NewGuid(), lot.Session())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Owner.Client.GetAsync($"/api/seeding-records/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task A_date_that_is_not_a_date_answers_msg_gen_01()
    {
        var setup = await factory.CycleAsync();
        var lot = await setup.Owner.Client.SeedLotAsync();
        var answer = await setup.Owner.Client.PostAsync(
            $"/api/cultivation-cycles/{setup.CycleId}/seeding-records",
            new StringContent(JsonSerializer.Serialize(new { seedingDate = "yesterday", seedLotId = lot.LineId, laneNumbers = new[] { 1 }, quantity = 1 }), System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01"), (answer.StatusCode, await answer.ErrorCodeAsync()));
    }
}
