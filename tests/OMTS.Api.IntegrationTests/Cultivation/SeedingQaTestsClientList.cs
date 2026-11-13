using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Common.Constants;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Cultivation;

using OMTS.Api.IntegrationTests.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

/// <summary>QA of PR C2 against its client list: what each section promises the web and mobile clients, and the views that read seeding from other features.</summary>
public class SeedingQaTestsClientList(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // Client list §1 Refusals, the exact text a client shows, with what it names (GBR-01, BR-CYCLE-04).
    [Fact]
    public async Task The_refusals_name_the_mark_the_closed_cycle_the_raft_and_the_zone()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        var raftCode = (await owner.SeedingListAsync(setup.CycleId)).Text("raftCode");
        await owner.MarkSeedingCompleteAsync(setup.CycleId);

        var again = await (await owner.CompletionPreviewAsync(setup.CycleId)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Equal($"The seeding of raft {raftCode} is marked complete on {Today:dd/MM/yyyy} and cannot be used for this action.", again.Text("detail"));

        var inDiscontinuedZone = await factory.CycleAsync();
        var zone = await inDiscontinuedZone.Owner.Client.GetFromJsonAsync<JsonElement>($"/api/zones/{inDiscontinuedZone.ZoneId}");
        await factory.UpdateAsync<FarmingZone>(inDiscontinuedZone.ZoneId, z => z.StatusId = StatusIds.Usage.Discontinued);
        var zoneRefusal = await (await inDiscontinuedZone.Owner.Client.PostSeedingAsync(inDiscontinuedZone.CycleId, lot.Session())).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Contains(zone.Text("code"), zoneRefusal.Text("detail"));

        var inDiscontinuedRaft = await factory.CycleAsync();
        var raft = await inDiscontinuedRaft.Owner.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{inDiscontinuedRaft.RaftId}");
        await factory.DiscontinueRaftAsync(inDiscontinuedRaft.RaftId);
        var raftRefusal = await (await inDiscontinuedRaft.Owner.Client.PostSeedingAsync(inDiscontinuedRaft.CycleId, lot.Session())).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Contains(raft.Text("code"), raftRefusal.Text("detail"));

        var closed = await factory.CycleAsync();
        var closedRaft = (await closed.Owner.Client.SeedingListAsync(closed.CycleId)).Text("raftCode");
        await factory.CloseCycleAsync(closed.CycleId);
        var cycleRefusal = await (await closed.Owner.Client.PostSeedingAsync(closed.CycleId, lot.Session())).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Contains(closedRaft, cycleRefusal.Text("detail"));
        Assert.Contains("Closed", cycleRefusal.Text("detail"));
    }

    // Client list §1: rowVersion missing or unreadable on the mark; a lane list of Lanes field; a repeated invalid list.
    [Fact]
    public async Task The_mark_needs_a_readable_version_and_every_seeding_route_is_closed_to_anonymous()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session());

        Assert.Equal(["RowVersion"], await (await owner.PostSeedingCompleteAsync(setup.CycleId, null)).InvalidFieldsAsync());
        Assert.Equal(["RowVersion"], await (await owner.PostSeedingCompleteAsync(setup.CycleId, "%%%")).InvalidFieldsAsync());
        Assert.Equal(["RowVersion"], await (await owner.PutSeedingAsync(record.Id(), record.AsCorrection() with { RowVersion = "%%%" })).InvalidFieldsAsync());
        Assert.Equal(["RowVersion"], await (await owner.RemoveSeedingAsync(record.Id(), rowVersion: "%%%")).InvalidFieldsAsync());

        var anonymous = factory.CreateClient();
        foreach (var answer in new[]
        {
            await anonymous.GetAsync($"/api/rafts/{setup.RaftId}/seed-lots"),
            await anonymous.GetAsync($"/api/rafts/{setup.RaftId}/lanes/1/progress"),
            await anonymous.GetAsync($"/api/cultivation-cycles/{setup.CycleId}/seeding-records"),
            await anonymous.PostSeedingAsync(setup.CycleId, lot.Session()),
            await anonymous.CompletionPreviewAsync(setup.CycleId),
            await anonymous.PostSeedingCompleteAsync(setup.CycleId),
            await anonymous.GetAsync($"/api/seeding-records/{record.Id()}"),
            await anonymous.PutSeedingAsync(record.Id(), record.AsCorrection()),
            await anonymous.RemoveSeedingAsync(record.Id()),
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
        }
    }

    // Client list §1: a quantity as on a receipt line: at most 2 decimals, however it is written; the edges.
    [Fact]
    public async Task A_quantity_written_with_trailing_zeros_counts_as_its_value_and_the_edges_hold()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();

        // The quantity travels as the text the client typed (a multipart field since 12/11/2026).
        async Task<HttpResponseMessage> PostRawAsync(string quantity, int lane) => await owner.PostAsync(
            $"/api/cultivation-cycles/{setup.CycleId}/seeding-records",
            StepFormKit.FormOf(new { seedingDate = Today, seedLotId = lot.LineId, laneNumbers = new[] { lane }, quantity = (object)null! }, photos: 1).WithField("quantity", quantity));

        await (await PostRawAsync("1.100", 1)).ReadAsync(HttpStatusCode.Created);
        await (await PostRawAsync("0.01", 2)).ReadAsync(HttpStatusCode.Created);
        await (await PostRawAsync("1000000000", 3)).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(["Quantity"], await (await PostRawAsync("0.001", 4)).InvalidFieldsAsync());
        Assert.Equal(["Quantity"], await (await PostRawAsync("1000000000.01", 4)).InvalidFieldsAsync());
        Assert.Equal(["Quantity"], await (await PostRawAsync("79228162514264337593543950335", 4)).InvalidFieldsAsync());
    }

    // Client list §2: the lane progress route: another raft's cycle and an unreadable cycle id.
    [Fact]
    public async Task The_lane_progress_of_another_rafts_cycle_is_404_and_an_unreadable_cycle_id_is_400()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var other = await owner.CreateRaftAsync(setup.ZoneId);
        var otherCycle = await owner.StartCycleAsync(other.Id(), new CycleBody(Today.AddDays(-1)));

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/rafts/{setup.RaftId}/lanes/1/progress?cycleId={otherCycle.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/api/rafts/{setup.RaftId}/lanes/1/progress?cycleId=junk")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/rafts/{setup.RaftId}/lanes/0/progress")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/rafts/{setup.RaftId}/lanes/-1/progress")).StatusCode);
    }

    // Client list §2: lanes show Growing on the raft view (FARM-04.1), and the zone list knows the seeding.
    [Fact]
    public async Task The_raft_views_and_the_zone_list_read_the_sessions()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session([1, 2]));

        var lanes = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{setup.RaftId}/lanes/today");
        Assert.Equal(["Growing", "Growing", "NotSeeded", "NotSeeded", "NotSeeded"], lanes.GetProperty("lanes").EnumerateArray().OrderBy(l => l.GetProperty("laneNumber").GetInt32()).Select(l => l.Text("state")));
        Assert.False((await owner.ZoneRowAsync(setup.ZoneId, setup.RaftId)).Flag("canCancel"));

        await (await owner.RemoveSeedingAsync(record.Id())).ReadAsync(HttpStatusCode.NoContent);

        var after = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{setup.RaftId}/lanes/today");
        Assert.All(after.GetProperty("lanes").EnumerateArray(), l => Assert.Equal("NotSeeded", l.Text("state")));
        Assert.True((await owner.ZoneRowAsync(setup.ZoneId, setup.RaftId)).Flag("canCancel"));
    }

    // Client list §4: the type on the preferences of an Owner only, push on by default and it can be turned off; the row stays in the list then.
    [Fact]
    public async Task The_new_notice_is_an_owner_preference_that_can_be_turned_off_and_stays_in_the_list()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var manager = await factory.ManagerAsync(setup.ZoneId);

        static async Task<Dictionary<string, JsonElement>> TypesAsync(HttpClient client) =>
            (await client.GetFromJsonAsync<JsonElement>("/api/me/notification-preferences")).GetProperty("types").EnumerateArray().ToDictionary(i => i.Text("type"));

        var mine = await TypesAsync(owner);
        Assert.True(mine["CYCLE_SEEDING_COMPLETED"].Flag("push"));
        Assert.DoesNotContain("CYCLE_SEEDING_COMPLETED", (await TypesAsync(manager.Client)).Keys);

        (await owner.PutAsJsonAsync("/api/me/notification-preferences", new { items = new[] { new { type = "CYCLE_SEEDING_COMPLETED", push = false } } })).EnsureSuccessStatusCode();
        await manager.Client.MarkSeedingCompleteAsync(setup.CycleId);

        Assert.Contains(await factory.SeedingNoticesAsync(setup.CycleId), n => n.RecipientAccountId == setup.Owner.Account.Id);
    }

    // Client list §5: the audit rows: values as codes and numbers, only the changed fields, the zone and the raft code.
    [Fact]
    public async Task The_audit_rows_hold_what_the_client_list_says()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var (first, second) = (await owner.SeedLotAsync(), await owner.SeedLotAsync());
        var record = await owner.SeedingRecordAsync(setup.CycleId, first.Session([2, 1], Today.AddDays(-2), 5.5m));
        await (await owner.PutSeedingAsync(record.Id(), record.AsCorrection("Wrong amount") with { Quantity = 6m })).ReadAsync(HttpStatusCode.OK);
        var corrected = await owner.GetSeedingAsync(record.Id());
        await (await owner.PutSeedingAsync(record.Id(), corrected.AsCorrection("Wrong lot") with { SeedLotId = second.LineId })).ReadAsync(HttpStatusCode.OK);
        await (await owner.RemoveSeedingAsync(record.Id(), "By mistake")).ReadAsync(HttpStatusCode.NoContent);
        await owner.MarkSeedingCompleteAsync(setup.CycleId);

        var rows = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == record.Id()).OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(["SeedingRecord.Created", "SeedingRecord.Corrected", "SeedingRecord.Corrected", "SeedingRecord.Removed"], rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.Equal(("SeedingRecord", record.Text("raftCode"), setup.ZoneId, setup.Owner.Account.Id), (r.RecordType, r.RecordCode, r.ZoneId!.Value, r.ActorAccountId!.Value)));

        using var created = JsonDocument.Parse(rows[0].After!);
        Assert.Equal(first.Code, created.RootElement.GetProperty("SeedLot").GetString());
        Assert.Equal([1, 2], created.RootElement.GetProperty("Lanes").EnumerateArray().Select(l => l.GetInt32()));
        Assert.Equal(5.5m, created.RootElement.GetProperty("Quantity").GetDecimal());

        Assert.Equal(["Quantity"], JsonDocument.Parse(rows[1].After!).RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Wrong amount", rows[1].Reason);
        Assert.Equal(["SeedLot"], JsonDocument.Parse(rows[2].Before!).RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(second.Code, JsonDocument.Parse(rows[3].Before!).RootElement.GetProperty("SeedLot").GetString());
        Assert.Equal("By mistake", rows[3].Reason);

        var mark = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == setup.CycleId && e.Action == "CultivationCycle.SeedingCompleted"));
        Assert.Equal(["LaneCount", "SeededCount", "SeedingCompletedOn"], JsonDocument.Parse(mark.After!).RootElement.EnumerateObject().Select(p => p.Name).Order());
    }

    // BR-CYCLE-01, BR-CYCLE-02 "one time per cycle", BR-FARM-03: the next cycle starts with every lane Not seeded and its own mark; the old sessions keep counting on the lot.
    [Fact]
    public async Task A_new_cycle_starts_clean_and_has_its_own_mark_and_the_closed_cycles_sessions_still_hold_the_lot()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(quantity: 50m, received: Today.AddDays(-40));
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([1, 2], Today.AddDays(-8), 20m));
        await owner.MarkSeedingCompleteAsync(setup.CycleId);
        var told = (await factory.SeedingNoticesAsync(setup.CycleId)).Count;
        await factory.UpdateAsync<CultivationCycle>(setup.CycleId, c =>
        {
            c.StatusId = StatusIds.CultivationCycle.Closed;
            c.CloseDate = Today.AddDays(-5);
        });

        var next = await owner.StartCycleAsync(setup.RaftId, new CycleBody(Today.AddDays(-4)));

        var list = await owner.SeedingListAsync(next.Id());
        Assert.Equal(("NotSeeded", 0), (list.StateOf(1), list.GetProperty("records").GetArrayLength()));
        Assert.True(list.Flag("canMarkComplete"));
        Assert.Equal(["Seeding date"], await (await owner.PostSeedingAsync(next.Id(), lot.Session([1], Today.AddDays(-5)))).InvalidFieldsAsync());
        await owner.RecordSeedingAsync(next.Id(), lot.Session([1], Today.AddDays(-4), 10m));
        var line = (await owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{lot.ReceiptId}")).LineViewOf(lot.ItemId);
        Assert.Equal((30m, 20m), (line.Money("seeded"), line.Money("returnable")));
        await (await owner.CancelAsync(lot.ReceiptId, "Wrong")).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-09");

        await owner.MarkSeedingCompleteAsync(next.Id());
        Assert.Equal(told, (await factory.SeedingNoticesAsync(next.Id())).Count);

        // The closed cycle keeps its own view of the lanes and its mark; the default lane view is the Open cycle.
        var old = await owner.SeedingListAsync(setup.CycleId);
        Assert.Equal(("Growing", "Growing", "NotSeeded"), (old.StateOf(1), old.StateOf(2), old.StateOf(3)));
        Assert.Equal(next.Id(), (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{setup.RaftId}/lanes/2/progress")).GetProperty("cycle").Id());
    }


    // Client list §1 Refusals and the routes: unknown ids, a removed session, the longest reason however it is spaced; the push follows the preference.
    [Fact]
    public async Task Unknown_ids_a_removed_session_and_the_longest_reason_answer_as_the_client_list_says()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session());

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/rafts/{Guid.NewGuid()}/seed-lots")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/cultivation-cycles/{Guid.NewGuid()}/seeding-records")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.CompletionPreviewAsync(Guid.NewGuid())).StatusCode);

        var spaced = $"  {new string('r', 1000)}  ";
        await (await owner.PutSeedingAsync(record.Id(), record.AsCorrection(spaced) with { Quantity = 11m })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(1000, await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == record.Id() && e.Action == "SeedingRecord.Corrected").Select(e => e.Reason!.Length).SingleAsync()));
        Assert.Equal(["Reason"], await (await owner.RemoveSeedingAsync(record.Id(), new string('r', 1001))).InvalidFieldsAsync());

        await (await owner.RemoveSeedingAsync(record.Id())).ReadAsync(HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.RemoveSeedingAsync(record.Id())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutSeedingAsync(record.Id(), record.AsCorrection())).StatusCode);
    }

    // Client list §2: without a cycle id the Open cycle, else the one started last, however the rows were saved.
    [Fact]
    public async Task The_lane_view_without_a_cycle_id_shows_the_cycle_started_last()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(received: Today.AddDays(-40));
        await factory.UpdateAsync<CultivationCycle>(setup.CycleId, c =>
        {
            c.StatusId = StatusIds.CultivationCycle.Closed;
            c.StartDate = Today.AddDays(-30);
            c.CloseDate = Today.AddDays(-20);
        });
        var later = await owner.StartCycleAsync(setup.RaftId, new CycleBody(Today.AddDays(-10)));
        await owner.RecordSeedingAsync(later.Id(), lot.Session([1], Today.AddDays(-9)));
        await factory.CloseCycleAsync(later.Id());

        var lane = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{setup.RaftId}/lanes/1/progress");

        Assert.Equal((later.Id(), "Growing"), (lane.GetProperty("cycle").Id(), lane.Text("state")));
    }

    // Purchase side, the rows the rewrite of the flow tests kept in other words: the export stays text, the list stays bounded (the removed PurchaseReceiptListExportTests).
    [Fact]
    public async Task The_receipt_export_never_writes_a_formula_and_the_period_stays_bounded()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var world = await factory.WorldAsync(owner);
        var supplier = await owner.CreateSupplierAsync(name: $"=1+1 {NewCode()}");
        await owner.CreateReceiptAsync(new ReceiptBody { SupplierId = supplier, Complete = true, Lines = [new LineBody(world.Other, 1m, 100m)] });

        var answer = await owner.GetAsync($"/api/purchase-receipts/export?supplierId={supplier}");
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        using var book = new ClosedXML.Excel.XLWorkbook(await answer.Content.ReadAsStreamAsync());
        var cell = book.Worksheets.First().CellsUsed(c => c.GetString().StartsWith("=1+1", StringComparison.Ordinal)).Single();
        Assert.False(cell.HasFormula);

        Assert.Equal(["To date"], await (await owner.GetAsync($"/api/purchase-receipts?from=2020-01-01&to=2026-01-02")).InvalidFieldsAsync());
    }

    // ---- PM calls, not defects: each test passes and pins what the branch does, so the answer is on record. ----

    // P1. GBR-04 "Seeding does not compare the quantity with what is left": there is no limit on a session, so sessions in sequence pass the lot, also after a return. Only a return has a limit.
    [Fact]
    public async Task P1_a_session_has_no_limit_it_may_pass_received_minus_returned_and_then_nothing_can_be_returned()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(quantity: 100m);
        await owner.ReturnLotAsync(lot, 60m);

        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([1], quantity: 60m));

        var line = (await owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{lot.ReceiptId}")).LineViewOf(lot.ItemId);
        Assert.Equal((60m, 60m, 0m), (line.Money("seeded"), line.Money("returnedQuantity"), line.Money("returnable")));
        await (await owner.PostReturnAsync(lot.ReceiptId, new ReturnBody { Lines = [new ReturnLineBody(lot.LineId, 0.01m)] })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-03");
    }

    // P2. A double tap on a slow link records the session twice: nothing tells two identical sessions apart (NFR-04 names SALE-05 and DEBT-02 only), and the seeded quantity lowers the return limit.
    [Fact]
    public async Task P2_two_identical_sessions_both_save_and_count_twice()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(quantity: 100m);

        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([1], quantity: 30m).Percents(50m, 1));
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([1], quantity: 30m).Percents(50m, 1));

        Assert.Equal(2, (await owner.SeedingListAsync(setup.CycleId)).GetProperty("records").GetArrayLength());
        Assert.Equal(40m, (await owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{lot.ReceiptId}")).LineViewOf(lot.ItemId).Money("returnable"));
    }

    // P3. MSG-CYCLE-03 says no completed receipt exists; it also shows when one exists and all of it was returned.
    [Fact]
    public async Task P3_the_no_lot_message_says_no_receipt_exists_when_the_only_lot_was_returned_in_whole()
    {
        var api = new AuthApiFactory();
        var owner = (await api.OwnerAsync()).Client;
        var raft = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id();
        var lot = await owner.SeedLotAsync(quantity: 5m);
        await owner.ReturnLotAsync(lot, 5m);

        var list = await owner.SeedLotsAsync(raft);

        Assert.Equal(("MSG-CYCLE-03", 0), (list.Text("messageCode"), list.GetProperty("lots").GetArrayLength()));
        Assert.Contains("no completed purchase receipt for seed exists yet", list.Text("message"));
    }

    // P4. GBR-01 "cannot be chosen for new records", and CYCLE-02 is not in its Applies To: a lot of a discontinued supplier stays offered and can be seeded (the item: the branch's own test).
    [Fact]
    public async Task P4_a_lot_of_a_discontinued_supplier_stays_offered_and_can_be_seeded()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        await factory.UpdateAsync<OMTS.Domain.Entities.Purchasing.Supplier>(lot.SupplierId, s => s.StatusId = StatusIds.Usage.Discontinued);

        Assert.Contains(lot.LineId, (await owner.SeedLotsAsync(setup.RaftId)).GetProperty("lots").EnumerateArray().Select(l => l.Id()));
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([1]));
    }

    // P5. A mark with no session at all tells the Owner "0 of 5 lanes have seed", and a removal after the mark leaves lanes Not seeded with the mark standing.
    [Fact]
    public async Task P5_a_mark_with_no_session_tells_the_owner_0_of_5_and_a_later_removal_keeps_the_mark()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var lot = await setup.Owner.Client.SeedLotAsync();
        await manager.Client.MarkSeedingCompleteAsync(setup.CycleId);
        var notice = (await factory.SeedingNoticesAsync(setup.CycleId)).First(n => n.RecipientAccountId == setup.Owner.Account.Id);
        Assert.Contains("0 of 5 lanes have seed", notice.Body);

        var record = await manager.Client.SeedingRecordAsync(setup.CycleId, lot.Session([1]));
        await (await manager.Client.RemoveSeedingAsync(record.Id())).ReadAsync(HttpStatusCode.NoContent);

        var list = await manager.Client.SeedingListAsync(setup.CycleId);
        Assert.Equal(("NotSeeded", Today), (list.StateOf(1), list.Date("seedingCompletedOn")));
    }

    // ---- Findings: each test fails on purpose and names the finding. ----

    // L1. MSG-GEN-06 "{count} records are still in use": the cancel always says 1, for one record or for four.
    [Fact]
    public async Task L1_the_refused_cancel_of_a_cycle_counts_the_records_that_hold_it()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([1]));
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([2]));
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([3]));
        await owner.RecordLogAsync(setup.CycleId);
        var cycle = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{setup.CycleId}");

        var refused = await (await owner.CancelCycleAsync(setup.CycleId, cycle.Text("rowVersion"))).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-06");

        Assert.StartsWith("Cannot proceed because 4 records are still in use", refused.Text("detail"));
    }
}
/// <summary>QA of the demo of PR C2 on SQL Server: what the client list §6 promises, session by session, and a rerun after a real removal.</summary>
public class SeedingQaTestsDemo
{
    [SqlServerFact]
    public async Task The_demo_holds_what_the_client_list_says_and_a_rerun_does_not_bring_back_a_removed_session()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-demo-files-{Guid.NewGuid():N}");
        try
        {
            await using var database = SqlServerTestDatabase.Create();
            await using var api = new SqlServerApiFactory(database, new Dictionary<string, string?>
            {
                [BootstrapSettings.CooperativeFullNameKey] = "HTX QA",
                [BootstrapSettings.OwnerDisplayNameKey] = "Owner QA",
                [BootstrapSettings.OwnerUsernameKey] = "owner",
                [BootstrapSettings.OwnerPhoneNumberKey] = "0900000001",
                [BootstrapSettings.OwnerTemporaryPasswordKey] = AcsTestKit.NewPassword(),
                ["Storage:Local:RootPath"] = storageRoot,
            });
            Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(api)).ExitCode);

            var byRaft = await UseDbAsync(api, async db => (await db.SeedingRecords.AsNoTracking().Include(r => r.Lanes).ThenInclude(l => l.Lane).Include(r => r.Cycle).ThenInclude(c => c.Raft).ToListAsync())
                .GroupBy(r => r.Cycle.Raft.Code).ToDictionary(g => g.Key, g => g.Select(r => r.Lanes.Select(l => l.Lane.LaneNumber).ToList()).ToList()));
            var laneCounts = await UseDbAsync(api, async db => (await db.Lanes.AsNoTracking().Where(l => l.StatusId == StatusIds.Lane.InUse).Select(l => new { l.Raft.Code, l.LaneNumber }).ToListAsync())
                .GroupBy(l => l.Code).ToDictionary(g => g.Key, g => g.Count()));

            foreach (var zone in new[] { "B1", "B2" })
            {
                // Two sessions covering every lane, the second re-hanging one; one session with the last lane left; one on every lane.
                var (first, second, third) = ($"{zone}-01", $"{zone}-02", $"{zone}-03");
                Assert.Equal(2, byRaft[first].Count);
                Assert.Equal(laneCounts[first], byRaft[first].SelectMany(l => l).Distinct().Count());
                Assert.Single(byRaft[first].SelectMany(l => l).GroupBy(l => l).Where(g => g.Count() == 2));
                Assert.Equal(laneCounts[second] - 1, Assert.Single(byRaft[second]).Count);
                Assert.DoesNotContain(laneCounts[second], byRaft[second][0]);
                Assert.Equal(laneCounts[third], Assert.Single(byRaft[third]).Count);
                Assert.DoesNotContain($"{zone}-04", byRaft.Keys);
                Assert.DoesNotContain($"{zone}-05", byRaft.Keys);
            }

            // The receipts of the lots can no longer be cancelled; nothing seeded in the demo passes what was received minus returned.
            var figures = await UseDbAsync(api, db => db.PurchaseReceiptLines.AsNoTracking()
                .Where(l => db.SeedingRecords.Any(s => s.SeedLotLineId == l.Id))
                .Select(l => new
                {
                    l.Receipt.Notes,
                    Received = l.Quantity!.Value,
                    Seeded = db.SeedingRecords.Where(s => s.SeedLotLineId == l.Id).Sum(s => s.Quantity),
                    Returned = db.PurchaseReturnLines.Where(r => r.ReceiptLineId == l.Id && r.Return.StatusId == StatusIds.PurchaseReturn.Recorded).Sum(r => (decimal?)r.Quantity) ?? 0,
                }).ToListAsync());
            Assert.Equal(["DEMO-R01", "DEMO-R02", "DEMO-R07", "DEMO-R08", "DEMO-R13"], figures.Select(f => f.Notes!.Split(' ')[0]).Order());
            Assert.All(figures, f => Assert.True(f.Seeded + f.Returned <= f.Received, $"{f.Notes}: {f.Seeded} seeded + {f.Returned} returned of {f.Received}"));

            // Real data removal: the lanes of B1-02 go back, the rerun keeps them so (the audit log remembers the session).
            var removed = await UseDbAsync(api, db => db.SeedingRecords.Where(r => r.Cycle.Raft.Code == "B1-02").Select(r => r.Id).SingleAsync());
            await UseDbAsync(api, async db =>
            {
                await db.SeedingRecordLanes.Where(l => l.SeedingRecordId == removed).ExecuteDeleteAsync();
                return await db.SeedingRecords.Where(r => r.Id == removed).ExecuteDeleteAsync();
            });
            var (sessions, audit, notices) = await UseDbAsync(api, async db => (await db.SeedingRecords.CountAsync(), await db.AuditEvents.CountAsync(), await db.Notifications.CountAsync()));

            Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(api)).ExitCode);

            Assert.Equal((sessions, audit, notices), await UseDbAsync(api, async db => (await db.SeedingRecords.CountAsync(), await db.AuditEvents.CountAsync(), await db.Notifications.CountAsync())));
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    private static async Task<T> UseDbAsync<T>(WebApplicationFactory<Program> api, Func<OmtsDbContext, Task<T>> query)
    {
        using var scope = api.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<OmtsDbContext>());
    }

    private static async Task<BootstrapOutcome> RunAsync(WebApplicationFactory<Program> api)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(true, CancellationToken.None);
    }
}
