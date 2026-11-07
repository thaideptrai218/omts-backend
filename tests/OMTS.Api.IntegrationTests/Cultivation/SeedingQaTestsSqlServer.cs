using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

using OMTS.Api.IntegrationTests.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

/// <summary>QA of CYCLE-02 on SQL Server: the queries, real versions and parallel saves. Every race answers 2xx or 409, never a 500, and no answer is untrue.</summary>
public class SeedingQaTestsSqlServer(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int Rounds = 8;

    private static async Task<(FarmingSqlServerHost Host, HttpClient Owner, Guid ZoneId, Guid RaftId, JsonElement Cycle)> StartAsync()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        return (host, owner, zoneId, raftId, await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10))));
    }

    private static async Task<JsonElement> NewCycleAsync(HttpClient owner, Guid zoneId)
    {
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        return await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)));
    }

    private static async Task AssertAnsweredAsync(string what, IEnumerable<HttpResponseMessage> answers)
    {
        foreach (var answer in answers)
        {
            var text = await answer.Content.ReadAsStringAsync();
            Assert.True(answer.IsSuccessStatusCode || (int)answer.StatusCode is 400 or 404 or 409, $"{what}: {(int)answer.StatusCode}: {text}");
        }
    }

    private static Task<decimal> SeededAsync(FarmingSqlServerHost host, Guid lineId) =>
        host.QueryAsync(db => db.SeedingRecords.Where(r => r.SeedLotLineId == lineId).Select(r => r.Quantity).SumAsync());

    private static Task<decimal> ReturnedAsync(FarmingSqlServerHost host, Guid lineId) =>
        host.QueryAsync(db => db.PurchaseReturnLines.Where(l => l.ReceiptLineId == lineId).Select(l => l.Quantity).SumAsync());

    // The offered lots are worked out in SQL: seeded, returned and the order, on SQL Server's decimals, also with no session and a Cancelled return.
    [SqlServerFact]
    public async Task The_seed_lot_list_runs_on_sql_server_with_decimal_sums_a_cancelled_return_and_the_order()
    {
        var (host, owner, _, raftId, cycle) = await StartAsync();
        await using var _host = host;
        var own = await owner.SeedLotAsync(quantity: 10.5m, received: Today.AddDays(-9), raftId: raftId);
        var newest = await owner.SeedLotAsync(quantity: 5m, received: Today.AddDays(-1));
        var older = await owner.SeedLotAsync(quantity: 5m, received: Today.AddDays(-2));
        var whole = await owner.SeedLotAsync(quantity: 0.01m);
        await owner.ReturnLotAsync(whole, 0.01m);
        var freed = await owner.SeedLotAsync(quantity: 3m, received: Today.AddDays(-30));
        var receipt = await owner.ReturnLotAsync(freed, 3m);
        await (await owner.CancelReturnAsync(receipt.GetProperty("returns")[0].Id(), "By mistake")).ReadAsync(HttpStatusCode.OK);
        await owner.RecordSeedingAsync(cycle.Id(), own.Session([1], quantity: 0.01m));
        await owner.RecordSeedingAsync(cycle.Id(), own.Session([2], quantity: 2.49m));

        var lots = (await owner.SeedLotsAsync(raftId)).GetProperty("lots").EnumerateArray().ToList();

        Assert.Equal([own.LineId, newest.LineId, older.LineId, freed.LineId], lots.Select(l => l.Id()));
        Assert.Equal((10.5m, 2.5m), (lots[0].Money("quantityReceived"), lots[0].Money("quantitySeeded")));
        Assert.Equal(0m, lots[1].Money("quantitySeeded"));
    }

    // Client list §1: a PUT that changes nothing answers 200, writes no audit row and moves no version, however the client writes the number (the column holds 10.000).
    [SqlServerFact]
    public async Task A_correction_that_writes_the_same_quantity_another_way_changes_nothing()
    {
        var (host, owner, _, _, cycle) = await StartAsync();
        await using var _host = host;
        var lot = await owner.SeedLotAsync();
        var record = await owner.SeedingRecordAsync(cycle.Id(), lot.Session(quantity: 10m));
        var read = await owner.GetSeedingAsync(record.Id());

        var same = await (await owner.PutSeedingAsync(record.Id(), read.AsCorrection() with { Quantity = 10m, LaneNumbers = [2, 1] })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(read.Text("rowVersion"), same.GetProperty("record").Text("rowVersion"));
        Assert.Equal(0, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "SeedingRecord.Corrected" && e.RecordId == record.Id())));
    }

    // GBR-02: the longest reason fits the audit log, on a correction and a removal.
    [SqlServerFact]
    public async Task A_reason_of_1000_characters_fits_the_audit_log()
    {
        var (host, owner, _, _, cycle) = await StartAsync();
        await using var _host = host;
        var lot = await owner.SeedLotAsync();
        var record = await owner.SeedingRecordAsync(cycle.Id(), lot.Session(quantity: 10m));
        var reason = new string('l', 1000);

        var saved = (await (await owner.PutSeedingAsync(record.Id(), record.AsCorrection(reason) with { Quantity = 11m })).ReadAsync(HttpStatusCode.OK)).GetProperty("record");
        await (await owner.RemoveSeedingAsync(record.Id(), reason, saved.Text("rowVersion"))).ReadAsync(HttpStatusCode.NoContent);

        var kept = await host.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == record.Id() && e.Reason != null).Select(e => e.Reason!.Length).ToListAsync());
        Assert.Equal([1000, 1000], kept);
    }

    // PURCHASE-02, GBR-04: a return that read the receipt before a session, or before a correction that raises or moves one, cannot save over it: the receipt row is part of each of them.
    [SqlServerFact]
    public async Task A_save_of_the_receipt_read_before_a_session_or_its_correction_loses_to_it()
    {
        var (host, owner, _, _, cycle) = await StartAsync();
        await using var _host = host;
        var (lot, target) = (await owner.SeedLotAsync(quantity: 100m), await owner.SeedLotAsync(quantity: 100m));

        async Task AssertLosesAsync(Guid receiptId, Func<Task> theSave)
        {
            await using var reading = host.Database.CreateContext();
            var read = await reading.PurchaseReceipts.SingleAsync(r => r.Id == receiptId);
            await theSave();
            reading.Entry(read).State = EntityState.Modified;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => reading.SaveChangesAsync());
        }

        JsonElement record = default;
        await AssertLosesAsync(lot.ReceiptId, async () => record = await owner.SeedingRecordAsync(cycle.Id(), lot.Session([1], quantity: 40m)));
        await AssertLosesAsync(lot.ReceiptId, async () => record = (await (await owner.PutSeedingAsync(record.Id(), record.AsCorrection() with { Quantity = 70m })).ReadAsync(HttpStatusCode.OK)).GetProperty("record"));
        await AssertLosesAsync(target.ReceiptId, async () => await (await owner.PutSeedingAsync(record.Id(), record.AsCorrection() with { SeedLotId = target.LineId })).ReadAsync(HttpStatusCode.OK));
    }

    // PURCHASE-02: a correction that raises a session against a return, in parallel: 2xx or 409, and each answer is what was saved.
    [SqlServerFact]
    public async Task A_correction_that_raises_a_session_and_a_return_answer_2xx_or_409_and_each_answer_is_true()
    {
        var (host, owner, _, _, cycle) = await StartAsync();
        await using var _host = host;
        for (var round = 0; round < Rounds; round++)
        {
            var lot = await owner.SeedLotAsync(quantity: 100m);
            var record = await owner.SeedingRecordAsync(cycle.Id(), lot.Session([(round % 5) + 1], quantity: 40m));

            var answers = await InParallelAsync(2, i => i == 0
                ? owner.PutSeedingAsync(record.Id(), record.AsCorrection() with { Quantity = 70m })
                : owner.PostReturnAsync(lot.ReceiptId, new ReturnBody { Lines = [new ReturnLineBody(lot.LineId, 50m)] }));

            await AssertAnsweredAsync($"round {round}", answers);
            Assert.Equal(answers[0].IsSuccessStatusCode ? 70m : 40m, await SeededAsync(host, lot.LineId));
            Assert.Equal(answers[1].StatusCode == HttpStatusCode.Created ? 50m : 0m, await ReturnedAsync(host, lot.LineId));
        }
    }

    // PURCHASE-06.2: a session against the Owner's correction that lowers the lot: 2xx or 409. A session after the correction may pass the lot (GBR-04); a correction after the session stops at it.
    [SqlServerFact]
    public async Task A_session_and_the_owners_correction_of_the_lot_answer_2xx_or_409()
    {
        var (host, owner, zoneId, _, _) = await StartAsync();
        await using var _host = host;
        for (var round = 0; round < Rounds; round++)
        {
            var cycle = await NewCycleAsync(owner, zoneId);
            var lot = await owner.SeedLotAsync(quantity: 100m);
            // Seeded already, so the receipt can only be corrected (BR-PURCHASE-09).
            await owner.RecordSeedingAsync(cycle.Id(), lot.Session([1], quantity: 10m));

            var answers = await InParallelAsync(2, i => i == 0
                ? owner.PostSeedingAsync(cycle.Id(), lot.Session([2], quantity: 40m))
                : owner.CorrectAsync(lot.ReceiptId, "Counted again", new { lineId = lot.LineId, quantity = 20m }));

            await AssertAnsweredAsync($"round {round}", answers);
            Assert.Equal(answers[0].StatusCode == HttpStatusCode.Created ? 50m : 10m, await SeededAsync(host, lot.LineId));
            var quantity = await host.QueryAsync(db => db.PurchaseReceiptLines.Where(l => l.Id == lot.LineId).Select(l => l.Quantity!.Value).SingleAsync());
            Assert.Equal(answers[1].IsSuccessStatusCode ? 20m : 100m, quantity);
        }
    }

    // GBR-04: a correction moving a session to a lot and the cancel of that receipt: never a cancelled lot with a session.
    [SqlServerFact]
    public async Task A_correction_to_a_lot_and_the_cancel_of_its_receipt_never_both_succeed()
    {
        var (host, owner, _, _, cycle) = await StartAsync();
        await using var _host = host;
        var first = await owner.SeedLotAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var target = await owner.SeedLotAsync();
            var record = await owner.SeedingRecordAsync(cycle.Id(), first.Session([(round % 5) + 1]));

            var answers = await InParallelAsync(2, i => i == 0
                ? owner.PutSeedingAsync(record.Id(), record.AsCorrection() with { SeedLotId = target.LineId })
                : owner.CancelAsync(target.ReceiptId, "Wrong"));

            await AssertAnsweredAsync($"round {round}", answers);
            Assert.False(answers.All(a => a.IsSuccessStatusCode), $"round {round}: moved to a lot whose receipt was cancelled");
            await owner.RemoveSeedingAsync(record.Id(), rowVersion: (await owner.GetSeedingAsync(record.Id())).Text("rowVersion"));
        }
    }

    // BR-PURCHASE-09: removing the last session and cancelling the receipt: a cancelled receipt has no session left, whichever comes first.
    [SqlServerFact]
    public async Task The_removal_of_the_last_session_and_the_cancel_of_its_receipt_leave_no_session_on_a_cancelled_lot()
    {
        var (host, owner, _, _, cycle) = await StartAsync();
        await using var _host = host;
        for (var round = 0; round < Rounds; round++)
        {
            var lot = await owner.SeedLotAsync();
            var record = await owner.SeedingRecordAsync(cycle.Id(), lot.Session([(round % 5) + 1]));

            var answers = await InParallelAsync(2, i => i == 0
                ? owner.RemoveSeedingAsync(record.Id(), rowVersion: record.Text("rowVersion"))
                : owner.CancelAsync(lot.ReceiptId, "Wrong"));

            await AssertAnsweredAsync($"round {round}", answers);
            var cancelled = await host.QueryAsync(db => db.PurchaseReceipts.AnyAsync(r => r.Id == lot.ReceiptId && r.StatusId == Domain.Constants.StatusIds.PurchaseReceipt.Cancelled));
            Assert.False(cancelled && await SeededAsync(host, lot.LineId) > 0, $"round {round}");
        }
    }

    // BR-CYCLE-02 after the mark: of a parallel mark and a re-hang one may pass, and the answers stay true; the mark is one time.
    [SqlServerFact]
    public async Task A_rehang_and_the_mark_never_leave_a_rehang_saved_after_the_mark_it_lost_to()
    {
        var (host, owner, zoneId, _, _) = await StartAsync();
        await using var _host = host;
        var lot = await owner.SeedLotAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var cycle = await NewCycleAsync(owner, zoneId);
            var first = await owner.RecordSeedingAsync(cycle.Id(), lot.Session([1]));

            var answers = await InParallelAsync(2, i => i == 0
                ? owner.PostSeedingAsync(cycle.Id(), lot.Session([1], quantity: 1m))
                : owner.PostSeedingCompleteAsync(cycle.Id(), first.Text("cycleRowVersion")));

            await AssertAnsweredAsync($"round {round}", answers);
            Assert.False(answers.All(a => a.IsSuccessStatusCode), $"round {round}: a re-hang and the mark both saved from one version of the cycle");
        }
    }

    // BR-CYCLE-01: a start date moved past a session being saved: never a session before the start.
    [SqlServerFact]
    public async Task A_session_and_a_new_start_date_never_leave_a_session_before_the_start()
    {
        var (host, owner, zoneId, _, _) = await StartAsync();
        await using var _host = host;
        var lot = await owner.SeedLotAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var cycle = await NewCycleAsync(owner, zoneId);

            var answers = await InParallelAsync(2, i => i == 0
                ? owner.PostSeedingAsync(cycle.Id(), lot.Session([1], Today.AddDays(-5)))
                : owner.PutCycleAsync(cycle.Id(), new CycleBody(Today.AddDays(-2), RowVersion: cycle.Text("rowVersion"))));

            await AssertAnsweredAsync($"round {round}", answers);
            var start = await host.QueryAsync(db => db.CultivationCycles.Where(c => c.Id == cycle.Id()).Select(c => c.StartDate).SingleAsync());
            var first = await host.QueryAsync(db => db.SeedingRecords.Where(r => r.CycleId == cycle.Id()).MinAsync(r => (DateOnly?)r.SeedingDate));
            Assert.True(first is null || first >= start, $"round {round}: first session {first}, start {start}");
        }
    }

    // BR-CYCLE-01: more rounds of the cancel against a session, on a clean cycle each time. The losers answer 404 or 409, never 500, and no session outlives its cycle.
    [SqlServerFact]
    public async Task A_session_and_the_cancel_of_the_cycle_stay_clean_over_many_rounds()
    {
        var (host, owner, zoneId, _, _) = await StartAsync();
        await using var _host = host;
        var lot = await owner.SeedLotAsync();
        for (var round = 0; round < Rounds * 2; round++)
        {
            var cycle = await NewCycleAsync(owner, zoneId);

            var answers = await InParallelAsync(2, i => i == 0 ? owner.PostSeedingAsync(cycle.Id(), lot.Session()) : owner.CancelCycleAsync(cycle.Id(), cycle.Text("rowVersion")));

            await AssertAnsweredAsync($"round {round}", answers);
            Assert.False(answers[0].StatusCode == HttpStatusCode.Created && answers[1].StatusCode == HttpStatusCode.NoContent, $"round {round}: both saved");
        }
    }

    // Several rafts seeding one lot while a return is made. The limit is that of a return only: GBR-04 does not compare a session with what is left.
    [SqlServerFact]
    public async Task Parallel_sessions_of_one_lot_on_several_rafts_and_a_return_answer_2xx_or_409_and_the_return_respects_what_it_read()
    {
        var (host, owner, zoneId, _, _) = await StartAsync();
        await using var _host = host;
        for (var round = 0; round < 4; round++)
        {
            var cycles = new List<JsonElement>();
            for (var i = 0; i < 4; i++)
            {
                cycles.Add(await NewCycleAsync(owner, zoneId));
            }

            var lot = await owner.SeedLotAsync(quantity: 100m);

            var answers = await InParallelAsync(5, i => i < 4
                ? owner.PostSeedingAsync(cycles[i].Id(), lot.Session([1], quantity: 30m))
                : owner.PostReturnAsync(lot.ReceiptId, new ReturnBody { Lines = [new ReturnLineBody(lot.LineId, 60m)] }));

            await AssertAnsweredAsync($"round {round}", answers);
            var created = answers.Take(4).Count(a => a.StatusCode == HttpStatusCode.Created);
            output.WriteLine($"round {round}: {created} of 4 sessions saved, the return answered {(int)answers[4].StatusCode}");
            Assert.Equal(created * 30m, await SeededAsync(host, lot.LineId));
            var returned = await ReturnedAsync(host, lot.LineId);
            Assert.Equal(answers[4].StatusCode == HttpStatusCode.Created ? 60m : 0m, returned);
            Assert.True(created >= 1 || returned > 0, $"round {round}: nobody saved");
        }
    }

    // Every read of the feature on SQL Server, Open and Closed: only the in-memory provider ran the lane routes before.
    [SqlServerFact]
    public async Task Every_read_route_runs_on_sql_server_for_an_open_and_a_closed_cycle()
    {
        var (host, owner, zoneId, raftId, cycle) = await StartAsync();
        await using var _host = host;
        var (first, second) = (await owner.SeedLotAsync(quantity: 10.5m, raftId: raftId), await owner.SeedLotAsync());
        await owner.RecordSeedingAsync(cycle.Id(), first.Session([1, 2], Today.AddDays(-5), 2.5m));
        var record = await owner.SeedingRecordAsync(cycle.Id(), second.Session([2], Today.AddDays(-3), 1m));
        var manager = await host.StaffInZoneAsync(zoneId);

        foreach (var client in new[] { owner, manager.Client })
        {
            var list = await client.SeedingListAsync(cycle.Id());
            Assert.Equal(("Growing", "Growing", "NotSeeded"), (list.StateOf(1), list.StateOf(2), list.StateOf(3)));
            Assert.Equal(2, list.GetProperty("lots").GetArrayLength());
            Assert.Equal(1m, (await client.GetSeedingAsync(record.Id())).Money("quantity"));
            Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftId}/lanes/2/progress")).GetProperty("sessions").GetArrayLength());
            Assert.Equal(2, (await client.ProgressAsync(raftId)).GetProperty("seedLots").GetArrayLength());
            Assert.Single((await client.ProgressAsync(raftId, $"?date={Today.AddDays(-4):yyyy-MM-dd}")).GetProperty("seedLots").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == 2).GetProperty("seedLots").EnumerateArray());
            await (await client.CompletionPreviewAsync(cycle.Id())).ReadAsync(HttpStatusCode.OK);
            await (await client.GetAsync($"/api/rafts/{raftId}/lanes/today")).ReadAsync(HttpStatusCode.OK);
            Assert.False((await client.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{cycle.Id()}")).Flag("canCancel"));
        }

        var line = (await owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{first.ReceiptId}")).LineViewOf(first.ItemId);
        Assert.Equal((2.5m, 8m), (line.Money("seeded"), line.Money("returnable")));

        await host.UseDbAsync(db =>
        {
            var row = db.CultivationCycles.Single(c => c.Id == cycle.Id());
            row.StatusId = Domain.Constants.StatusIds.CultivationCycle.Closed;
            row.CloseDate = Today.AddDays(-1);
        });

        var closed = await owner.SeedingListAsync(cycle.Id());
        Assert.False(closed.Flag("canRecord"));
        Assert.Equal("Growing", closed.StateOf(2));
        await (await owner.GetAsync($"/api/rafts/{raftId}/lanes/2/progress?cycleId={cycle.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(2, (await owner.ProgressAsync(raftId, $"?cycleId={cycle.Id()}")).GetProperty("seedLots").GetArrayLength());
    }

    // The versions a save answers are the versions to send next, with real row versions: the session, the mark after a session and after a correction.
    [SqlServerFact]
    public async Task The_versions_in_each_answer_work_for_the_next_save()
    {
        var (host, owner, zoneId, _, cycle) = await StartAsync();
        await using var _host = host;
        var lot = await owner.SeedLotAsync();

        var saved = await owner.RecordSeedingAsync(cycle.Id(), lot.Session([1]));
        await (await owner.PostSeedingCompleteAsync(cycle.Id(), saved.Text("cycleRowVersion"))).ReadAsync(HttpStatusCode.OK);

        var other = await NewCycleAsync(owner, zoneId);
        var second = await owner.RecordSeedingAsync(other.Id(), lot.Session([1]));
        var corrected = await (await owner.PutSeedingAsync(second.GetProperty("record").Id(), second.GetProperty("record").AsCorrection() with { Quantity = 3m })).ReadAsync(HttpStatusCode.OK);
        var marked = await (await owner.PostSeedingCompleteAsync(other.Id(), corrected.Text("cycleRowVersion"))).ReadAsync(HttpStatusCode.OK);

        // The cycle the mark answers can be edited with its own version; a garbage version is 400; a well-formed old one is MSG06.
        await owner.EditCycleAsync(other.Id(), new CycleBody(Today.AddDays(-10), TargetYieldKg: 5m, RowVersion: marked.GetProperty("cycle").Text("rowVersion")));
        var removedRecord = corrected.GetProperty("record");
        Assert.Equal(["RowVersion"], await (await owner.RemoveSeedingAsync(removedRecord.Id(), rowVersion: "%%%")).InvalidFieldsAsync());
        Assert.Equal(["RowVersion"], await (await owner.RemoveSeedingAsync(removedRecord.Id(), rowVersion: "")).InvalidFieldsAsync());
        await (await owner.RemoveSeedingAsync(removedRecord.Id(), rowVersion: second.GetProperty("record").Text("rowVersion"))).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        await (await owner.RemoveSeedingAsync(removedRecord.Id(), rowVersion: removedRecord.Text("rowVersion"))).ReadAsync(HttpStatusCode.NoContent);
    }

    // CYCLE-02.1 "notified once": the push too, not only the row, whoever lost the parallel marks; a lost mark pushes nothing.
    [SqlServerFact]
    public async Task Parallel_marks_push_to_the_owner_once()
    {
        var (host, owner, zoneId, _, _) = await StartAsync();
        await using var _host = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        var inbox = host.Api.Services.GetRequiredService<OMTS.Infrastructure.Messaging.Fakes.FakePushSender>().Inbox;
        var before = inbox.List(host.Owner.Account.Id.ToString()).Count;

        for (var round = 0; round < 4; round++)
        {
            var cycle = await NewCycleAsync(owner, zoneId);
            await AssertAnsweredAsync($"round {round}", await InParallelAsync(4, _ => manager.Client.PostSeedingCompleteAsync(cycle.Id(), cycle.Text("rowVersion"))));
            Assert.Equal(before + round + 1, inbox.List(host.Owner.Account.Id.ToString()).Count);
        }
    }

    // GBR-04 over more rounds than the branch's own tests: a session against a return, and against the cancel of its receipt.
    [SqlServerFact]
    public async Task A_session_and_a_return_or_a_cancel_of_its_receipt_stay_clean_over_many_rounds()
    {
        var (host, owner, _, _, cycle) = await StartAsync();
        await using var _host = host;
        for (var round = 0; round < Rounds * 2; round++)
        {
            var lot = await owner.SeedLotAsync(quantity: 100m);
            var answers = await InParallelAsync(2, i => i == 0
                ? owner.PostSeedingAsync(cycle.Id(), lot.Session([(round % 5) + 1], quantity: 60m))
                : round % 2 == 0 ? owner.PostReturnAsync(lot.ReceiptId, new ReturnBody { Lines = [new ReturnLineBody(lot.LineId, 60m)] }) : owner.CancelAsync(lot.ReceiptId, "Wrong"));

            await AssertAnsweredAsync($"round {round}", answers);
            Assert.Equal(answers[0].StatusCode == HttpStatusCode.Created ? 60m : 0m, await SeededAsync(host, lot.LineId));
            if (round % 2 == 1)
            {
                Assert.False(answers.All(a => a.IsSuccessStatusCode), $"round {round}: the receipt was cancelled and its lot seeded");
            }
        }
    }

    // Client list §3: a receipt form open while a session was saved answers MSG06 when it sends its version; a form re-read saves. MSG06 names who saved and gives the new version.
    [SqlServerFact]
    public async Task A_receipt_form_read_before_a_session_answers_msg06_with_the_new_version_and_a_new_read_saves()
    {
        var (host, owner, _, _, cycle) = await StartAsync();
        await using var _host = host;
        var lot = await owner.SeedLotAsync(quantity: 100m);
        await owner.RecordSeedingAsync(cycle.Id(), lot.Session([1], quantity: 10m));
        var read = await owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{lot.ReceiptId}");

        await owner.RecordSeedingAsync(cycle.Id(), lot.Session([2], quantity: 10m));

        object Body(string version) => new { reason = "Counted again", rowVersion = version, lines = new[] { new { lineId = lot.LineId, unitPrice = 900m } } };
        var stale = await (await owner.PostAsJsonAsync($"/api/purchase-receipts/{lot.ReceiptId}/correct", Body(read.Text("rowVersion")))).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        var fresh = await owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{lot.ReceiptId}");
        Assert.Equal(fresh.Text("rowVersion"), stale.Text("currentRowVersion"));
        await (await owner.PostAsJsonAsync($"/api/purchase-receipts/{lot.ReceiptId}/correct", Body(fresh.Text("rowVersion")))).ReadAsync(HttpStatusCode.OK);

        var staleMark = await (await owner.PostSeedingCompleteAsync(cycle.Id(), cycle.Text("rowVersion"))).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        Assert.Equal((await owner.SeedingListAsync(cycle.Id())).Text("cycleRowVersion"), staleMark.Text("currentRowVersion"));
    }

    // The purchase list under the merge: a huge page on SQL Server answers an empty page, the offset does not overflow (the removed SQL_list_pages test).
    [SqlServerFact]
    public async Task A_huge_page_of_the_receipt_list_answers_empty_on_sql_server()
    {
        var (host, owner, _, _, _) = await StartAsync();
        await using var _host = host;
        await owner.SeedLotAsync();

        var page = await owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts?page={int.MaxValue}&pageSize=100&from={Today.AddDays(-20):yyyy-MM-dd}&to={Today:yyyy-MM-dd}");

        Assert.Empty(page.GetProperty("page").GetProperty("items").EnumerateArray());
        Assert.True(page.GetProperty("totals").GetProperty("count").GetInt32() >= 1);
    }

    // P6, a PM call. Sessions of one lot on different rafts conflict with nothing the users edit, yet the receipt row is part of each save: those in the same moment answer 409 MSG06 and must be sent again.
    // The client list states it. This pins that a loser saved nothing and that sending it again saves.
    [SqlServerFact]
    public async Task P6_sessions_of_one_lot_on_different_rafts_in_the_same_moment_answer_msg06_and_saved_nothing_and_a_new_send_saves()
    {
        var (host, owner, zoneId, _, _) = await StartAsync();
        await using var _host = host;
        for (var round = 0; round < 4; round++)
        {
            var cycles = new List<JsonElement>();
            for (var i = 0; i < 4; i++)
            {
                cycles.Add(await NewCycleAsync(owner, zoneId));
            }

            var lot = await owner.SeedLotAsync(quantity: 100m);

            var answers = await InParallelAsync(4, i => owner.PostSeedingAsync(cycles[i].Id(), lot.Session([1], quantity: 1m)));

            var saved = answers.Count(a => a.StatusCode == HttpStatusCode.Created);
            output.WriteLine($"round {round}: {saved} of 4 sessions saved");
            Assert.True(saved >= 1);
            Assert.Equal(saved * 1m, await SeededAsync(host, lot.LineId));
            for (var i = 0; i < 4; i++)
            {
                if (answers[i].StatusCode != HttpStatusCode.Created)
                {
                    Assert.Equal("MSG06", (await answers[i].ReadAsync(HttpStatusCode.Conflict)).Text("code"));
                    await owner.RecordSeedingAsync(cycles[i].Id(), lot.Session([1], quantity: 1m));
                }
            }

            Assert.Equal(4m, await SeededAsync(host, lot.LineId));
        }
    }
}
