using System.Net;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>CYCLE-02.2 and BR-CYCLE-02: a Zone Manager corrects or removes what they recorded, the Owner any session, with a reason, while the cycle is Open.</summary>
public class SeedingCorrectionTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task A_zone_manager_corrects_only_their_own_session_and_the_owner_any()
    {
        var setup = await factory.CycleAsync();
        var (recorder, colleague) = (await factory.ManagerAsync(setup.ZoneId), await factory.ManagerAsync(setup.ZoneId));
        var lot = await setup.Owner.Client.SeedLotAsync();
        var ofRecorder = await recorder.Client.SeedingRecordAsync(setup.CycleId, lot.Session([1]));
        var ofOwner = await setup.Owner.Client.SeedingRecordAsync(setup.CycleId, lot.Session([2]));

        // The flag says what the writes answer.
        Assert.True((await recorder.Client.GetSeedingAsync(ofRecorder.Id())).Flag("canCorrect"));
        Assert.False((await colleague.Client.GetSeedingAsync(ofRecorder.Id())).Flag("canCorrect"));
        Assert.False((await recorder.Client.GetSeedingAsync(ofOwner.Id())).Flag("canCorrect"));
        Assert.True((await setup.Owner.Client.GetSeedingAsync(ofRecorder.Id())).Flag("canCorrect"));

        var edit = ofRecorder.AsCorrection() with { Quantity = 11m };
        Assert.Equal("MSG-GEN-08", await (await colleague.Client.PutSeedingAsync(ofRecorder.Id(), edit)).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await colleague.Client.RemoveSeedingAsync(ofRecorder.Id())).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await recorder.Client.PutSeedingAsync(ofOwner.Id(), edit)).ErrorCodeAsync());

        var saved = await (await recorder.Client.PutSeedingAsync(ofRecorder.Id(), edit)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("MSG-GEN-05", 11m), (saved.Text("messageCode"), saved.GetProperty("record").GetProperty("quantity").GetDecimal()));

        var byOwner = await (await setup.Owner.Client.PutSeedingAsync(ofRecorder.Id(), edit with { Quantity = 12m })).ReadAsync(HttpStatusCode.OK);
        // The recorder stays who first recorded it, so they can still correct it.
        Assert.Equal((12m, recorder.Account.Id), (byOwner.GetProperty("record").GetProperty("quantity").GetDecimal(), byOwner.GetProperty("record").GetProperty("recordedByAccountId").GetGuid()));
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Owner.Client.RemoveSeedingAsync(ofRecorder.Id())).StatusCode);
    }

    [Fact]
    public async Task A_correction_changes_the_date_lot_lanes_and_quantity_with_the_reason_in_the_audit_log()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var (first, second) = (await owner.SeedLotAsync(), await owner.SeedLotAsync());
        var record = await owner.SeedingRecordAsync(setup.CycleId, first.Session([1, 2], Today.AddDays(-3), 10m));

        var saved = (await (await owner.PutSeedingAsync(record.Id(), new SeedingBody(Today.AddDays(-2), second.LineId, [2, 3], 20m, "Wrong lot", record.Text("rowVersion")))).ReadAsync(HttpStatusCode.OK)).GetProperty("record");

        Assert.Equal((Today.AddDays(-2), second.Code, 20m), (saved.Date("seedingDate"), saved.GetProperty("seedLot").Text("code"), saved.GetProperty("quantity").GetDecimal()));
        Assert.Equal([2, 3], saved.Lanes());
        var list = await owner.SeedingListAsync(setup.CycleId);
        Assert.Equal(("NotSeeded", "Growing", "Growing"), (list.StateOf(1), list.StateOf(2), list.StateOf(3)));

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "SeedingRecord.Corrected" && e.RecordId == record.Id()));
        Assert.Equal("Wrong lot", audit.Reason);
        Assert.Contains(first.Code, audit.Before);
        Assert.Contains(second.Code, audit.After);
    }

    [Fact]
    public async Task A_correction_checks_the_fields_as_a_new_session_and_needs_a_reason()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session());
        var edit = record.AsCorrection();

        foreach (var reason in new[] { null, "   ", new string('x', 1001) })
        {
            Assert.Equal(["Reason"], await (await owner.PutSeedingAsync(record.Id(), edit with { Reason = reason })).InvalidFieldsAsync());
        }

        Assert.Equal(["Seeding date"], await (await owner.PutSeedingAsync(record.Id(), edit with { SeedingDate = Today.AddDays(1) })).InvalidFieldsAsync());
        Assert.Equal(["Seeding date"], await (await owner.PutSeedingAsync(record.Id(), edit with { SeedingDate = Today.AddDays(-11) })).InvalidFieldsAsync());
        Assert.Equal(["Quantity"], await (await owner.PutSeedingAsync(record.Id(), edit with { Quantity = 0m })).InvalidFieldsAsync());
        Assert.Equal(["Lanes"], await (await owner.PutSeedingAsync(record.Id(), edit with { LaneNumbers = [] })).InvalidFieldsAsync());
        Assert.Equal(["Seed lot"], await (await owner.PutSeedingAsync(record.Id(), edit with { SeedLotId = Guid.NewGuid() })).InvalidFieldsAsync());
        Assert.Equal(["RowVersion"], await (await owner.PutSeedingAsync(record.Id(), edit with { RowVersion = null })).InvalidFieldsAsync());
        Assert.Equal(["Reason"], await (await owner.RemoveSeedingAsync(record.Id(), reason: " ")).InvalidFieldsAsync());
        Assert.Equal(["RowVersion"], await (await owner.RemoveSeedingAsync(record.Id(), rowVersion: null)).InvalidFieldsAsync());

        var kept = await owner.GetSeedingAsync(record.Id());
        Assert.Equal(10m, kept.GetProperty("quantity").GetDecimal());
        Assert.Equal([1, 2], kept.Lanes());
    }

    // A session on a lot that was received later than the new date, or the receipt date as the limit of a correction that keeps the lot.
    [Fact]
    public async Task A_correction_that_keeps_the_lot_still_checks_the_date_against_when_it_was_received()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(received: Today.AddDays(-4));
        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session());

        Assert.Equal(["Seeding date"], await (await owner.PutSeedingAsync(record.Id(), record.AsCorrection() with { SeedingDate = Today.AddDays(-5) })).InvalidFieldsAsync());
    }

    [Fact]
    public async Task A_correction_that_changes_nothing_answers_200_and_writes_no_audit_row()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session());

        await (await owner.PutSeedingAsync(record.Id(), record.AsCorrection())).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(0, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "SeedingRecord.Corrected" && e.RecordId == record.Id())));
    }

    [Fact]
    public async Task A_removal_takes_the_lanes_back_keeps_the_session_in_the_audit_log_and_lets_the_cycle_be_cancelled()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session([1, 2]));
        var cycle = await owner.SeedingListAsync(setup.CycleId);
        Assert.Equal("MSG-GEN-06", await (await owner.CancelCycleAsync(setup.CycleId, cycle.Text("cycleRowVersion"))).ErrorCodeAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.RemoveSeedingAsync(record.Id(), "Recorded by mistake")).StatusCode);

        var list = await owner.SeedingListAsync(setup.CycleId);
        Assert.Equal(("NotSeeded", 0), (list.StateOf(1), list.GetProperty("records").GetArrayLength()));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/seeding-records/{record.Id()}")).StatusCode);
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "SeedingRecord.Removed" && e.RecordId == record.Id()));
        Assert.Equal("Recorded by mistake", audit.Reason);
        Assert.Contains(lot.Code, audit.Before);
        Assert.Equal(0, await factory.QueryAsync(db => db.SeedingRecordLanes.CountAsync(l => l.SeedingRecordId == record.Id())));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.CancelCycleAsync(setup.CycleId, list.Text("cycleRowVersion"))).StatusCode);
    }

    // BR-CYCLE-04: a closed cycle is locked for every role; GBR-01: a Discontinued raft is read-only. The records stay readable.
    [Theory]
    [InlineData("closed")]
    [InlineData("discontinued")]
    [InlineData("zone discontinued")]
    public async Task A_closed_cycle_or_a_discontinued_raft_is_read_only_for_everyone(string how)
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var lot = await setup.Owner.Client.SeedLotAsync();
        var record = await manager.Client.SeedingRecordAsync(setup.CycleId, lot.Session());
        await (how switch
        {
            "closed" => factory.CloseCycleAsync(setup.CycleId),
            "discontinued" => factory.DiscontinueRaftAsync(setup.RaftId),
            _ => factory.UpdateAsync<FarmingZone>(setup.ZoneId, z => z.StatusId = StatusIds.Usage.Discontinued),
        });

        foreach (var client in new[] { manager.Client, setup.Owner.Client })
        {
            Assert.Equal("MSG-GEN-09", await (await client.PostSeedingAsync(setup.CycleId, lot.Session([3]))).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-09", await (await client.PutSeedingAsync(record.Id(), record.AsCorrection() with { Quantity = 1m })).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-09", await (await client.RemoveSeedingAsync(record.Id())).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-09", await (await client.PostSeedingCompleteAsync(setup.CycleId)).ErrorCodeAsync());

            var list = await client.SeedingListAsync(setup.CycleId);
            Assert.False(list.Flag("canRecord") || list.Flag("canMarkComplete"));
            Assert.All(list.GetProperty("lanes").EnumerateArray(), l => Assert.False(l.Flag("canSeed")));
            Assert.False(Assert.Single(list.GetProperty("records").EnumerateArray()).Flag("canCorrect"));
        }
    }
}
