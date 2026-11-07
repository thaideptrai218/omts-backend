using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Entities.Cultivation;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

using OMTS.Api.IntegrationTests.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

/// <summary>CYCLE-02 on SQL Server: real row versions, the constraints and parallel saves. Every race answers 2xx or 409, never a 500.</summary>
public class SeedingRaceTests
{
    private const int Rounds = 4;

    private static async Task<(FarmingSqlServerHost Host, HttpClient Owner, Guid ZoneId, Guid RaftId, JsonElement Cycle)> StartAsync()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        return (host, owner, zoneId, raftId, await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10))));
    }

    /// <summary>A new raft of the zone with its cycle, so each round of a race starts clean.</summary>
    private static async Task<(Guid RaftId, JsonElement Cycle)> NewCycleAsync(HttpClient owner, Guid zoneId)
    {
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        return (raftId, await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10))));
    }

    private static async Task AssertAnsweredAsync(IEnumerable<HttpResponseMessage> answers)
    {
        foreach (var answer in answers)
        {
            var text = await answer.Content.ReadAsStringAsync();
            Assert.True(answer.IsSuccessStatusCode || answer.StatusCode == HttpStatusCode.Conflict, $"{(int)answer.StatusCode}: {text}");
        }
    }

    [SqlServerFact]
    public async Task A_session_moves_the_versions_of_the_cycle_and_the_receipt_and_a_stale_version_answers_msg06()
    {
        var (host, owner, _, _, cycle) = await StartAsync();
        await using var _host = host;
        var lot = await owner.SeedLotAsync();
        var record = await owner.RecordSeedingAsync(cycle.Id(), lot.Session());

        Assert.NotEqual(cycle.Text("rowVersion"), record.Text("cycleRowVersion"));
        var saved = record.GetProperty("record");
        Assert.Equal(8, Convert.FromBase64String(saved.Text("rowVersion")).Length);
        var receipt = await owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{lot.ReceiptId}");
        Assert.NotEqual(lot.Receipt.Text("rowVersion"), receipt.Text("rowVersion"));

        // An open cycle form is stale now, as with a log.
        Assert.Equal("MSG06", await (await owner.PutCycleAsync(cycle.Id(), new CycleBody(Today.AddDays(-10), RowVersion: cycle.Text("rowVersion")))).ErrorCodeAsync());

        // Lanes only: the session still gets a new version, so the second of two forms is refused and shows the new one.
        var edit = saved.AsCorrection() with { LaneNumbers = [1, 3] };
        var corrected = (await (await owner.PutSeedingAsync(saved.Id(), edit)).ReadAsync(HttpStatusCode.OK)).GetProperty("record");
        Assert.NotEqual(saved.Text("rowVersion"), corrected.Text("rowVersion"));
        var stale = await (await owner.PutSeedingAsync(saved.Id(), edit with { Quantity = 1m })).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        Assert.Equal(corrected.Text("rowVersion"), stale.Text("currentRowVersion"));
        await (await owner.RemoveSeedingAsync(saved.Id(), rowVersion: saved.Text("rowVersion"))).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        await (await owner.PostSeedingCompleteAsync(cycle.Id(), cycle.Text("rowVersion"))).ProblemAsync(HttpStatusCode.Conflict, "MSG06");

        // Nothing changes: no new version, of the session or the cycle.
        var cycleNow = (await owner.SeedingListAsync(cycle.Id())).Text("cycleRowVersion");
        var same = await (await owner.PutSeedingAsync(saved.Id(), corrected.AsCorrection())).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((corrected.Text("rowVersion"), cycleNow), (same.GetProperty("record").Text("rowVersion"), same.Text("cycleRowVersion")));
    }

    [SqlServerFact]
    public async Task The_database_holds_the_quantity_above_0_the_lot_the_cycle_and_the_lanes_with_no_cascade()
    {
        var (host, owner, _, _, cycle) = await StartAsync();
        await using var _host = host;
        var lot = await owner.SeedLotAsync();
        var record = await owner.SeedingRecordAsync(cycle.Id(), lot.Session());
        await using var db = host.Database.CreateContext();

        var quantity = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlAsync($"UPDATE SeedingRecords SET Quantity = 0 WHERE Id = {record.Id()}"));
        Assert.Contains("CK_SeedingRecord_Quantity", quantity.Message);
        foreach (var sql in new FormattableString[]
        {
            $"DELETE FROM PurchaseReceiptLines WHERE Id = {lot.LineId}",
            $"DELETE FROM CultivationCycles WHERE Id = {cycle.Id()}",
            $"DELETE FROM SeedingRecords WHERE Id = {record.Id()}",
            $"DELETE FROM Lanes WHERE Id IN (SELECT LaneId FROM SeedingRecordLanes WHERE SeedingRecordId = {record.Id()})",
        })
        {
            Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlAsync(sql))).Number);
        }

        var duplicate = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlAsync($"INSERT INTO SeedingRecordLanes (SeedingRecordId, LaneId) SELECT SeedingRecordId, LaneId FROM SeedingRecordLanes WHERE SeedingRecordId = {record.Id()}"));
        Assert.Equal(2627, duplicate.Number);
    }

    [SqlServerFact]
    public async Task Parallel_marks_tell_the_owner_once_and_save_one_date()
    {
        var (host, owner, zoneId, _, _) = await StartAsync();
        await using var _host = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        for (var round = 0; round < Rounds; round++)
        {
            var (_, cycle) = await NewCycleAsync(owner, zoneId);

            var answers = await InParallelAsync(4, _ => manager.Client.PostSeedingCompleteAsync(cycle.Id(), cycle.Text("rowVersion")));

            await AssertAnsweredAsync(answers);
            Assert.Equal(1, answers.Count(a => a.StatusCode == HttpStatusCode.OK));
            var told = await host.QueryAsync(db => db.Notifications.CountAsync(n => n.RelatedRecordId == cycle.Id() && n.Type == "CYCLE_SEEDING_COMPLETED"));
            Assert.Equal(1, told);
        }
    }

    [SqlServerFact]
    public async Task Parallel_sessions_on_one_cycle_answer_2xx_or_409_and_each_answer_is_true()
    {
        var (host, owner, zoneId, _, _) = await StartAsync();
        await using var _host = host;
        var lot = await owner.SeedLotAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var (_, cycle) = await NewCycleAsync(owner, zoneId);

            var answers = await InParallelAsync(4, i => owner.PostSeedingAsync(cycle.Id(), lot.Session([i + 1])));

            await AssertAnsweredAsync(answers);
            Assert.Contains(answers, a => a.StatusCode == HttpStatusCode.Created);
            Assert.Equal(answers.Count(a => a.StatusCode == HttpStatusCode.Created), await host.QueryAsync(db => db.SeedingRecords.CountAsync(r => r.CycleId == cycle.Id())));
        }
    }

    [SqlServerFact]
    public async Task A_session_and_the_cancel_of_the_cycle_never_both_succeed()
    {
        var (host, owner, zoneId, _, _) = await StartAsync();
        await using var _host = host;
        var lot = await owner.SeedLotAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var (_, cycle) = await NewCycleAsync(owner, zoneId);

            var answers = await InParallelAsync(2, i => i == 0 ? owner.PostSeedingAsync(cycle.Id(), lot.Session()) : owner.CancelCycleAsync(cycle.Id(), cycle.Text("rowVersion")));

            // A session after the cancel finds no cycle.
            await AssertAnsweredAsync(answers.Where(a => a.StatusCode != HttpStatusCode.NotFound));
            var saved = await host.QueryAsync(db => db.SeedingRecords.CountAsync(r => r.CycleId == cycle.Id()));
            var exists = await host.QueryAsync(db => db.CultivationCycles.AnyAsync(c => c.Id == cycle.Id()));
            Assert.True(exists || saved == 0, $"round {round}: a session without its cycle");
            Assert.Equal(answers[0].StatusCode == HttpStatusCode.Created ? 1 : 0, saved);
        }
    }

    // GBR-04: either the receipt is cancelled, or the lot is seeded; never both.
    [SqlServerFact]
    public async Task A_session_and_the_cancel_of_its_receipt_never_both_succeed()
    {
        var (host, owner, _, _, cycle) = await StartAsync();
        await using var _host = host;
        for (var round = 0; round < Rounds; round++)
        {
            var lot = await owner.SeedLotAsync();

            var answers = await InParallelAsync(2, i => i == 0 ? owner.PostSeedingAsync(cycle.Id(), lot.Session([(round % 5) + 1])) : owner.CancelAsync(lot.ReceiptId, "Wrong"));

            Assert.All(answers, a => Assert.NotEqual(HttpStatusCode.InternalServerError, a.StatusCode));
            Assert.False(answers.All(a => a.IsSuccessStatusCode), $"round {round}: the receipt was cancelled and its lot seeded");
        }
    }

    // GBR-04: received − seeded − returned is never passed, whichever save comes first.
    [SqlServerFact]
    public async Task A_session_and_a_return_of_one_lot_never_pass_what_was_received()
    {
        var (host, owner, _, _, cycle) = await StartAsync();
        await using var _host = host;
        for (var round = 0; round < Rounds; round++)
        {
            var lot = await owner.SeedLotAsync(quantity: 100m);

            var answers = await InParallelAsync(2, i => i == 0
                ? owner.PostSeedingAsync(cycle.Id(), lot.Session([(round % 5) + 1], quantity: 60m))
                : owner.PostReturnAsync(lot.ReceiptId, new ReturnBody { Lines = [new ReturnLineBody(lot.LineId, 60m)] }));

            // The return after a session answers its limit (400); of two in the very same moment one answers MSG06. A session after a return may pass the lot (GBR-04).
            Assert.All(answers, a => Assert.NotEqual(HttpStatusCode.InternalServerError, a.StatusCode));
            var seeded = await host.QueryAsync(db => db.SeedingRecords.Where(r => r.SeedLotLineId == lot.LineId).SumAsync(r => r.Quantity));
            var returned = await host.QueryAsync(db => db.PurchaseReturnLines.Where(l => l.ReceiptLineId == lot.LineId).SumAsync(l => l.Quantity));
            Assert.Equal((answers[0].StatusCode == HttpStatusCode.Created ? 60m : 0m, answers[1].StatusCode == HttpStatusCode.Created ? 60m : 0m), (seeded, returned));
        }
    }

    [SqlServerFact]
    public async Task A_correction_and_a_removal_of_one_session_answer_2xx_or_409_with_nothing_half_saved()
    {
        var (host, owner, _, _, cycle) = await StartAsync();
        await using var _host = host;
        var lot = await owner.SeedLotAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var record = await owner.SeedingRecordAsync(cycle.Id(), lot.Session([(round % 5) + 1]));

            var answers = await InParallelAsync(3, i => i switch
            {
                0 => owner.PutSeedingAsync(record.Id(), record.AsCorrection() with { Quantity = 3m }),
                1 => owner.RemoveSeedingAsync(record.Id(), rowVersion: record.Text("rowVersion")),
                _ => owner.PutSeedingAsync(record.Id(), record.AsCorrection() with { LaneNumbers = [(round % 5) + 1, ((round + 1) % 5) + 1] }),
            });

            await AssertAnsweredAsync(answers.Where(a => a.StatusCode != HttpStatusCode.NotFound));
            var removed = answers[1].StatusCode == HttpStatusCode.NoContent;
            Assert.Equal(!removed, await host.QueryAsync(db => db.SeedingRecords.AnyAsync(r => r.Id == record.Id())));
            Assert.True(answers.Count(a => a.IsSuccessStatusCode) <= 1);
        }
    }

    /// <summary>No Close exists yet (C5): a close that read the cycle before a session cannot save over it.</summary>
    [SqlServerFact]
    public async Task A_close_that_read_the_cycle_before_a_session_loses_to_it()
    {
        var (host, owner, _, _, cycle) = await StartAsync();
        await using var _host = host;
        var lot = await owner.SeedLotAsync();
        await using var closing = host.Database.CreateContext();
        var read = await closing.CultivationCycles.SingleAsync(c => c.Id == cycle.Id());

        await owner.RecordSeedingAsync(cycle.Id(), lot.Session());

        (read.StatusId, read.CloseDate) = (Domain.Constants.StatusIds.CultivationCycle.Closed, Today);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => closing.SaveChangesAsync());
        Assert.Equal(1, await host.QueryAsync(db => db.Set<SeedingRecord>().CountAsync(r => r.CycleId == cycle.Id())));
    }
}
