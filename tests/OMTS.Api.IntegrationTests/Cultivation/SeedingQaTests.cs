using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Cultivation;

using OMTS.Api.IntegrationTests.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

/// <summary>QA of PR C2 against the SRS: dates, start-date moves, seed lots, quantities with the purchase limits, lanes and the mark.</summary>
public class SeedingQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static Task<JsonElement> ReceiptAsync(HttpClient client, Guid receiptId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{receiptId}");

    // BR-CYCLE-01: a new start date must not be after the first session, whichever session is first after corrections and removals.
    [Fact]
    public async Task The_start_date_follows_the_first_session_as_it_is_corrected_and_removed()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(received: Today.AddDays(-40));
        var early = await owner.SeedingRecordAsync(setup.CycleId, lot.Session([1], Today.AddDays(-6)));
        var late = await owner.SeedingRecordAsync(setup.CycleId, lot.Session([2], Today.AddDays(-3)));

        async Task<string> VersionAsync() => (await owner.SeedingListAsync(setup.CycleId)).Text("cycleRowVersion");

        // On the first session is allowed; a day after it is not; earlier is always allowed.
        await owner.EditCycleAsync(setup.CycleId, new CycleBody(Today.AddDays(-6), RowVersion: await VersionAsync()));
        Assert.Equal(["Start date"], await (await owner.PutCycleAsync(setup.CycleId, new CycleBody(Today.AddDays(-5), RowVersion: await VersionAsync()))).InvalidFieldsAsync());
        await owner.EditCycleAsync(setup.CycleId, new CycleBody(Today.AddDays(-30), RowVersion: await VersionAsync()));

        // A session may now go back to the new start, and that holds the start back.
        var moved = (await (await owner.PutSeedingAsync(early.Id(), early.AsCorrection() with { SeedingDate = Today.AddDays(-30) })).ReadAsync(HttpStatusCode.OK)).GetProperty("record");
        Assert.Equal(["Start date"], await (await owner.PutCycleAsync(setup.CycleId, new CycleBody(Today.AddDays(-29), RowVersion: await VersionAsync()))).InvalidFieldsAsync());

        // Removing it leaves the later session as the first.
        await (await owner.RemoveSeedingAsync(moved.Id(), rowVersion: moved.Text("rowVersion"))).ReadAsync(HttpStatusCode.NoContent);
        await owner.EditCycleAsync(setup.CycleId, new CycleBody(Today.AddDays(-3), RowVersion: await VersionAsync()));

        // After the start moved forward, a correction cannot go before it, and a new session neither.
        Assert.Equal(["Seeding date"], await (await owner.PutSeedingAsync(late.Id(), late.AsCorrection() with { SeedingDate = Today.AddDays(-4) })).InvalidFieldsAsync());
        Assert.Equal(["Seeding date"], await (await owner.PostSeedingAsync(setup.CycleId, lot.Session([3], Today.AddDays(-4)))).InvalidFieldsAsync());
    }

    // BR-CYCLE-01 names the first seeding session or cultivation log: the earlier of the two holds the start back.
    [Fact]
    public async Task The_start_date_stops_at_the_earlier_of_the_first_session_and_the_first_log()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([1], Today.AddDays(-3)));
        await owner.RecordLogAsync(setup.CycleId, new LogFields { VisitDate = Today.AddDays(-5) });

        var version = (await owner.SeedingListAsync(setup.CycleId)).Text("cycleRowVersion");

        Assert.Equal(["Start date"], await (await owner.PutCycleAsync(setup.CycleId, new CycleBody(Today.AddDays(-4), RowVersion: version))).InvalidFieldsAsync());
        await owner.EditCycleAsync(setup.CycleId, new CycleBody(Today.AddDays(-5), RowVersion: version));
    }

    // GBR-04: lots bought for this raft first, then the other seed lots, also those bought for a raft of another zone. Nothing says which raft they were for.
    [Fact]
    public async Task A_lot_bought_for_a_raft_of_another_zone_is_offered_after_the_own_lots_and_can_be_seeded()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var zoneB = (await owner.CreateZoneAsync()).Id();
        var raftB = (await owner.CreateRaftAsync(zoneB)).Id();
        var otherRaft = await owner.SeedLotAsync(received: Today.AddDays(-1), raftId: raftB);
        var own = await owner.SeedLotAsync(received: Today.AddDays(-20), raftId: setup.RaftId);
        var manager = await factory.ManagerAsync(setup.ZoneId);

        var offered = (await manager.Client.SeedLotsAsync(setup.RaftId)).GetProperty("lots").EnumerateArray().ToList();

        var (ownIndex, otherIndex) = (offered.FindIndex(l => l.Id() == own.LineId), offered.FindIndex(l => l.Id() == otherRaft.LineId));
        Assert.True(ownIndex >= 0 && otherIndex > ownIndex, $"own at {ownIndex}, other at {otherIndex}");
        Assert.True(offered[ownIndex].Flag("boughtForThisRaft"));
        Assert.False(offered[otherIndex].Flag("boughtForThisRaft"));
        Assert.DoesNotContain(raftB.ToString(), offered[otherIndex].GetRawText());
        await manager.Client.RecordSeedingAsync(setup.CycleId, otherRaft.Session([1], Today));
    }

    // CYCLE-02.0 step 3: the quantity already seeded is the total of the lot, over every raft, and follows corrections and removals.
    [Fact]
    public async Task The_quantity_seeded_shown_counts_every_raft_and_follows_corrections_and_removals()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var other = await owner.CreateRaftAsync(setup.ZoneId);
        var otherCycle = await owner.StartCycleAsync(other.Id(), new CycleBody(Today.AddDays(-10)));
        var lot = await owner.SeedLotAsync(quantity: 100m);

        async Task<decimal> ShownAsync() =>
            (await owner.SeedLotsAsync(setup.RaftId)).GetProperty("lots").EnumerateArray().Single(l => l.Id() == lot.LineId).Money("quantitySeeded");

        var first = await owner.SeedingRecordAsync(setup.CycleId, lot.Session([1], quantity: 1.25m));
        var second = await owner.SeedingRecordAsync(otherCycle.Id(), lot.Session([1], quantity: 2.5m));
        Assert.Equal(3.75m, await ShownAsync());

        await (await owner.PutSeedingAsync(first.Id(), first.AsCorrection() with { Quantity = 0.01m })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(2.51m, await ShownAsync());

        await (await owner.RemoveSeedingAsync(second.Id())).ReadAsync(HttpStatusCode.NoContent);
        Assert.Equal(0.01m, await ShownAsync());
    }

    // BR-PURCHASE-07, GBR-04: received âˆ’ seeded âˆ’ returned, in decimals, across several sessions, under correction and removal.
    [Fact]
    public async Task The_return_limit_holds_in_decimals_across_sessions_corrections_and_removals()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var other = await owner.CreateRaftAsync(setup.ZoneId);
        var otherCycle = await owner.StartCycleAsync(other.Id(), new CycleBody(Today.AddDays(-10)));
        var lot = await owner.SeedLotAsync(quantity: 10.1m);
        var elsewhere = await owner.SeedLotAsync(quantity: 5m);
        var first = await owner.SeedingRecordAsync(setup.CycleId, lot.Session([1], quantity: 3.33m));
        var second = await owner.SeedingRecordAsync(otherCycle.Id(), lot.Session([1], quantity: 3.33m));

        async Task<decimal> ReturnableAsync() => (await ReceiptAsync(owner, lot.ReceiptId)).LineViewOf(lot.ItemId).Money("returnable");

        Assert.Equal(3.44m, await ReturnableAsync());
        var refused = await (await owner.PostReturnAsync(lot.ReceiptId, new ReturnBody { Lines = [new ReturnLineBody(lot.LineId, 3.45m)] })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-03");
        Assert.Equal((3.44m, 6.66m, 0m), (refused.Money("maxQuantity"), refused.Money("seeded"), refused.Money("returned")));
        Assert.Contains("at most 3.44 kg", refused.Text("detail"));

        var returned = await owner.ReturnLotAsync(lot, 3.44m);
        Assert.Equal(0m, await ReturnableAsync());

        // A correction may pass what is left (GBR-04); the limit stays at 0, never below.
        var raised = (await (await owner.PutSeedingAsync(first.Id(), first.AsCorrection() with { Quantity = 5m })).ReadAsync(HttpStatusCode.OK)).GetProperty("record");
        Assert.Equal(0m, await ReturnableAsync());

        // Lowering, moving to another lot and removing each give back exactly their quantity.
        await (await owner.PutSeedingAsync(raised.Id(), raised.AsCorrection() with { Quantity = 3m })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(0.33m, await ReturnableAsync());
        await (await owner.PutSeedingAsync(first.Id(), new SeedingBody(Today.AddDays(-1), elsewhere.LineId, [1], 3m, "Wrong lot", ""))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(3.33m, await ReturnableAsync());
        Assert.Equal(2m, (await ReceiptAsync(owner, elsewhere.ReceiptId)).LineViewOf(elsewhere.ItemId).Money("returnable"));
        await (await owner.RemoveSeedingAsync(second.Id())).ReadAsync(HttpStatusCode.NoContent);
        Assert.Equal(6.66m, await ReturnableAsync());

        // The Owner's cancellation of the return frees it too (BR-PURCHASE-07).
        await (await owner.CancelReturnAsync(returned.GetProperty("returns")[0].Id(), "By mistake")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(10.1m, await ReturnableAsync());
    }

    // BR-PURCHASE-09, MSG-PURCHASE-10: the quantity not below seeded + returned, in decimals; only the Owner.
    [Fact]
    public async Task A_correction_of_the_receipt_stops_at_seeded_plus_returned_and_only_the_owner_corrects()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(quantity: 100m);
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session(quantity: 30.5m));
        await owner.ReturnLotAsync(lot, 10.25m);

        var below = await (await owner.CorrectAsync(lot.ReceiptId, "Counted again", new { lineId = lot.LineId, quantity = 40.74m })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-10");
        Assert.Equal(40.75m, below.Money("minQuantity"));
        Assert.Contains("cannot be less than 40.75 kg (30.5 seeded, 10.25 returned)", below.Text("detail"));

        var manager = await factory.ManagerAsync(setup.ZoneId);
        Assert.Equal("MSG-GEN-08", await (await manager.Client.CorrectAsync(lot.ReceiptId, "Counted again", new { lineId = lot.LineId, quantity = 50m })).ErrorCodeAsync());
        var accountant = await factory.StaffAsync(null, StaffRole.Accountant);
        Assert.Equal("MSG-GEN-08", await (await accountant.Client.CorrectAsync(lot.ReceiptId, "Counted again", new { lineId = lot.LineId, quantity = 50m })).ErrorCodeAsync());

        await (await owner.CorrectAsync(lot.ReceiptId, "Counted again", new { lineId = lot.LineId, quantity = 40.75m })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(0m, (await ReceiptAsync(owner, lot.ReceiptId)).LineViewOf(lot.ItemId).Money("returnable"));
    }

    // BR-PURCHASE-09: a receipt with two seed lines names the line that was seeded; seeded is told before returned.
    [Fact]
    public async Task The_cancel_block_names_the_seeded_line_and_goes_with_the_last_session()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var (firstItem, secondItem) = (await owner.CreateItemAsync("Seed", "kg"), await owner.CreateItemAsync("Seed", "kg"));
        var receipt = await owner.CreateReceiptAsync(new ReceiptBody
        {
            SupplierId = await owner.CreateSupplierAsync(), ReceiptDate = Today.AddDays(-10), Complete = true,
            Lines = [new LineBody(firstItem, 10m, 1m), new LineBody(secondItem, 10m, 1m)],
        });
        var code = receipt.Text("code");
        var record = await owner.SeedingRecordAsync(setup.CycleId, new SeedingBody(Today, receipt.LineOf(secondItem), [1], 1m));
        await (await owner.PostReturnAsync(receipt.Id(), new ReturnBody { Lines = [new ReturnLineBody(receipt.LineOf(firstItem), 1m)] })).ReadAsync(HttpStatusCode.Created);

        var refused = await (await owner.CancelAsync(receipt.Id(), "Wrong")).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-09");

        Assert.Equal(("Seeded", $"{code}-2"), (refused.Text("cause"), refused.Text("causeCode")));
        Assert.Equal($"Receipt {code} cannot be cancelled: seed lot {code}-2 has already been used. The Cooperative Owner can correct it instead.", refused.Text("detail"));

        await (await owner.RemoveSeedingAsync(record.Id())).ReadAsync(HttpStatusCode.NoContent);
        Assert.Equal("Returned", (await (await owner.CancelAsync(receipt.Id(), "Wrong")).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-09")).Text("cause"));
    }

    // CYCLE-02.0 step 5 on a correction: the date against the new lot; a lot that is not a seed lot is refused.
    [Fact]
    public async Task A_correction_to_another_lot_checks_that_lot_as_a_new_choice()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session([1], Today.AddDays(-5)));
        var edit = record.AsCorrection();

        var later = await owner.SeedLotAsync(received: Today.AddDays(-2));
        Assert.Equal(["Seeding date"], await (await owner.PutSeedingAsync(record.Id(), edit with { SeedLotId = later.LineId })).InvalidFieldsAsync());

        var draft = await owner.SeedLotAsync(complete: false);
        Assert.Equal(["Seed lot"], await (await owner.PutSeedingAsync(record.Id(), edit with { SeedLotId = draft.LineId })).InvalidFieldsAsync());

        var cancelled = await owner.SeedLotAsync();
        await (await owner.CancelAsync(cancelled.ReceiptId, "Wrong")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(["Seed lot"], await (await owner.PutSeedingAsync(record.Id(), edit with { SeedLotId = cancelled.LineId })).InvalidFieldsAsync());

        var whole = await owner.SeedLotAsync(quantity: 5m);
        await owner.ReturnLotAsync(whole, 5m);
        Assert.Equal(["Seed lot"], await (await owner.PutSeedingAsync(record.Id(), edit with { SeedLotId = whole.LineId })).InvalidFieldsAsync());

        Assert.Equal(lot.LineId, (await owner.GetSeedingAsync(record.Id())).GetProperty("seedLot").Id());
    }

    // BR-CYCLE-02 after the mark: only lanes Not seeded; a lane of the session stays; no new notice for anything that follows.
    [Fact]
    public async Task After_the_mark_corrections_and_removals_follow_the_lane_rules_and_tell_nobody()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session([1, 2]));
        var other = await owner.SeedingRecordAsync(setup.CycleId, lot.Session([3]));
        await owner.MarkSeedingCompleteAsync(setup.CycleId);
        var told = (await factory.SeedingNoticesAsync(setup.CycleId)).Count;
        var edit = record.AsCorrection();

        // Re-hanging is over: a new session or an added lane that already has seed is refused and named.
        var refused = await (await owner.PostSeedingAsync(setup.CycleId, lot.Session([3, 4]))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        Assert.Equal(("Lanes", "Lanes 3 cannot be seeded now."), (refused.Text("field"), refused.Text("hint")));
        Assert.Equal("Lanes", (await (await owner.PutSeedingAsync(record.Id(), edit with { LaneNumbers = [1, 2, 3] })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01")).Text("field"));

        // A lane of the session stays allowed, a lane Not seeded may be added, and a lane given back shows Not seeded.
        var saved = (await (await owner.PutSeedingAsync(record.Id(), edit with { LaneNumbers = [2, 4] })).ReadAsync(HttpStatusCode.OK)).GetProperty("record");
        var list = await owner.SeedingListAsync(setup.CycleId);
        Assert.Equal(("NotSeeded", "Growing", "Growing"), (list.StateOf(1), list.StateOf(2), list.StateOf(4)));
        Assert.Equal([1, 5], list.Numbers("notSeededLanes"));
        Assert.Equal(("Growing", true), (list.StateOf(3), Assert.Single(list.GetProperty("lanes").EnumerateArray(), l => l.GetProperty("laneNumber").GetInt32() == 1).Flag("canSeed")));
        Assert.False(Assert.Single(list.GetProperty("lanes").EnumerateArray(), l => l.GetProperty("laneNumber").GetInt32() == 2).Flag("canSeed"));

        // The lane given back can be seeded again, and a removal gives its lanes back too.
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([1]));
        await (await owner.RemoveSeedingAsync(other.Id())).ReadAsync(HttpStatusCode.NoContent);
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([3]));

        // The mark stays and nobody is told again.
        Assert.Equal(told, (await factory.SeedingNoticesAsync(setup.CycleId)).Count);
        Assert.Equal(Today, (await owner.SeedingListAsync(setup.CycleId)).Date("seedingCompletedOn"));
        Assert.Equal(saved.Id(), record.Id());
    }

    // CYCLE-02.0 step 7: the offer is about every lane, whichever save made them all seeded: a correction too, and not a correction that gives one back.
    [Fact]
    public async Task The_offer_to_mark_follows_a_correction_in_both_directions()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session([1, 2]));
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([3, 4]));

        var all = await (await owner.PutSeedingAsync(record.Id(), record.AsCorrection() with { LaneNumbers = [1, 2, 5] })).ReadAsync(HttpStatusCode.OK);
        Assert.True(all.Flag("offerMarkComplete"));

        var back = await (await owner.PutSeedingAsync(record.Id(), all.GetProperty("record").AsCorrection() with { LaneNumbers = [1] })).ReadAsync(HttpStatusCode.OK);
        Assert.False(back.Flag("offerMarkComplete"));
        Assert.Equal([2, 5], (await owner.SeedingListAsync(setup.CycleId)).Numbers("notSeededLanes"));
    }

    // CYCLE-02.1: a locked Owner is not told; every active Owner is told when a manager marks; the manager is not.
    [Fact]
    public async Task A_manager_mark_tells_every_active_owner_and_no_locked_one()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var other = await factory.OwnerAsync();
        var locked = await factory.OwnerAsync();
        await factory.UpdateAsync<Account>(locked.Account.Id, a => a.StatusId = StatusIds.Account.Locked);

        await manager.Client.MarkSeedingCompleteAsync(setup.CycleId);

        var told = (await factory.SeedingNoticesAsync(setup.CycleId)).Select(n => n.RecipientAccountId!.Value).ToList();
        Assert.Contains(setup.Owner.Account.Id, told);
        Assert.Contains(other.Account.Id, told);
        Assert.DoesNotContain(locked.Account.Id, told);
        Assert.DoesNotContain(manager.Account.Id, told);
    }

    // BR-FARM-04, BR-CYCLE-02: through the real raft edit: a lane Removed after the close keeps its record; a new cycle never seeds it, and neither count holds it.
    [Fact]
    public async Task A_lane_removed_after_a_closed_cycle_keeps_its_session_and_the_next_cycle_cannot_seed_it()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(received: Today.AddDays(-30));
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([5], Today.AddDays(-5)));
        await factory.UpdateAsync<CultivationCycle>(setup.CycleId, c =>
        {
            c.StatusId = StatusIds.CultivationCycle.Closed;
            c.CloseDate = Today.AddDays(-3);
        });
        var raft = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{setup.RaftId}");

        var edited = await owner.EditRaftAsync(setup.RaftId, NewRaftBody(raft.Text("code"), lanes: 4) with { RowVersion = raft.Text("rowVersion") });
        Assert.Equal([5], edited.LaneNumbers(StatusIds.Lane.Removed));

        var closed = await owner.SeedingListAsync(setup.CycleId);
        Assert.Equal(("Growing", 4, 0), (closed.StateOf(5), closed.GetProperty("laneCount").GetInt32(), closed.GetProperty("seededCount").GetInt32()));
        var lane = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{setup.RaftId}/lanes/5/progress?cycleId={setup.CycleId}");
        Assert.Single(lane.GetProperty("sessions").EnumerateArray());

        var next = await owner.StartCycleAsync(setup.RaftId, new CycleBody(Today.AddDays(-2)));
        var list = await owner.SeedingListAsync(next.Id());
        Assert.Equal(4, list.GetProperty("laneCount").GetInt32());
        Assert.DoesNotContain(5, list.GetProperty("lanes").EnumerateArray().Select(l => l.GetProperty("laneNumber").GetInt32()));
        Assert.Equal(["Lanes"], await (await owner.PostSeedingAsync(next.Id(), lot.Session([5], Today))).InvalidFieldsAsync());
        await owner.RecordSeedingAsync(next.Id(), lot.Session([4], Today));
    }

    // CYCLE-05: after a correction the views read the corrected date and lots, and on a date before it the lane is Not seeded (BR-FARM-03).
    [Fact]
    public async Task The_progress_of_a_date_follows_a_corrected_session()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var (first, second) = (await owner.SeedLotAsync(), await owner.SeedLotAsync());
        var record = await owner.SeedingRecordAsync(setup.CycleId, first.Session([1], Today.AddDays(-5)));

        await (await owner.PutSeedingAsync(record.Id(), record.AsCorrection() with { SeedingDate = Today.AddDays(-2), SeedLotId = second.LineId })).ReadAsync(HttpStatusCode.OK);

        var before = await owner.ProgressAsync(setup.RaftId, $"?date={Today.AddDays(-3):yyyy-MM-dd}");
        Assert.Equal("NotSeeded", before.GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == 1).Text("state"));
        Assert.Empty(before.GetProperty("seedLots").EnumerateArray());
        var now = await owner.ProgressAsync(setup.RaftId);
        var hung = now.GetProperty("seedLots").EnumerateArray().Single().GetProperty("seedLots").EnumerateArray().Single();
        Assert.Equal((second.LineId, second.Code), (hung.GetProperty("seedLotId").GetGuid(), hung.Text("code")));
    }

    // Malformed requests answer 4xx, never 500, on every write route.
    [Fact]
    public async Task Malformed_bodies_on_every_write_route_answer_400_and_save_nothing()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session());
        // The session itself is a multipart form since 12/11/2026 (photos beside it): an unreadable value is refused by name, and a JSON body is no longer accepted there.
        var routes = new (string Method, string Url)[]
        {
            ("POST", $"/api/cultivation-cycles/{setup.CycleId}/seeding-records"),
            ("PUT", $"/api/seeding-records/{record.Id()}"),
        };
        var fieldValues = new (string Field, string Value)[]
        {
            ("laneNumbers", "x"), ("laneNumbers", "1.5"), ("laneNumbers", "99999999999"), ("quantity", "many"), ("quantity", "1e400"), ("seedLotId", "not-a-guid"), ("seedingDate", "2026-02-30"),
            ("seedingDate", "20260101"), ("lanePercents", "[1,2]"), ("lanePercents", "{\"x\":1}"), ("lanePercents", "{\"1\":\"many\"}"),
        };

        foreach (var (method, url) in routes)
        {
            foreach (var (field, value) in fieldValues)
            {
                // The unreadable value replaces the field of the valid form (a repeated single-value field would read its first value).
                var body = new Dictionary<string, object?>
                {
                    ["seedingDate"] = Today.AddDays(-1), ["seedLotId"] = lot.LineId, ["laneNumbers"] = new[] { 1 }, ["quantity"] = 1, ["reason"] = "x", ["rowVersion"] = "",
                };
                body[field] = value;
                var form = StepFormKit.FormOf(body, photos: 1);
                var answer = await owner.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = form });
                Assert.True(answer.StatusCode == HttpStatusCode.BadRequest, $"{method} {url} with {field}={value}: {(int)answer.StatusCode} {await answer.Content.ReadAsStringAsync()}");
            }

            var json = await owner.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") });
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, json.StatusCode);
        }

        foreach (var body in new[] { "", "null", "{", "{\"reason\":1}", "{\"rowVersion\":[]}" })
        {
            foreach (var url in new[] { $"/api/seeding-records/{record.Id()}/remove", $"/api/cultivation-cycles/{setup.CycleId}/seeding-complete" })
            {
                var answer = await owner.PostAsync(url, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
                Assert.True(answer.StatusCode == HttpStatusCode.BadRequest, $"{url} with {body}: {(int)answer.StatusCode} {await answer.Content.ReadAsStringAsync()}");
            }
        }

        Assert.Equal(1, await factory.QueryAsync(db => db.SeedingRecords.CountAsync(r => r.CycleId == setup.CycleId)));
        Assert.Null((await owner.SeedingListAsync(setup.CycleId)).GetProperty("seedingCompletedOn").ValueKind == JsonValueKind.Null ? null : "marked");
    }

    // BR-CYCLE-02 "one or more lanes": a request of every lane number at once, far beyond the raft, is refused and answers quickly.
    [Fact]
    public async Task A_huge_or_negative_lane_list_is_refused()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();

        // A form carries fewer than 1,024 fields (the host's limit), so a "huge" list is 900 lanes here; more is refused before the service, still with a 400.
        Assert.Equal(["Lanes"], await (await owner.PostSeedingAsync(setup.CycleId, lot.Session(Enumerable.Range(1, 900).ToList()))).InvalidFieldsAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostSeedingAsync(setup.CycleId, lot.Session(Enumerable.Range(1, 5000).ToList()))).StatusCode);
        Assert.Equal(["Lanes"], await (await owner.PostSeedingAsync(setup.CycleId, lot.Session([1, int.MaxValue]))).InvalidFieldsAsync());
        Assert.Equal(["Lanes"], await (await owner.PostSeedingAsync(setup.CycleId, lot.Session([int.MinValue]))).InvalidFieldsAsync());
    }
}
