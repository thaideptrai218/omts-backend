using System.Net;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>§I.4.4 TRACE-03 (Owner Full, Zone Manager Full in the zone, Accountant No) and SCR-TRACE-02: the cycles of a raft and the records of a cycle that can be corrected only here.</summary>
public class TraceCorrectionAccessTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<List<HttpResponseMessage>> EveryCallAsync(HttpClient client, TracedRaft t, Guid logId) =>
    [
        await client.GetAsync($"/api/rafts/{t.RaftId}/trace-corrections"),
        await client.GetAsync($"/api/cultivation-cycles/{t.CycleId}/trace-corrections"),
        await client.PreviewSeedingAsync(t.SessionId, t.Session.SeedingCorrection(quantity: 12m)),
        await client.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(quantity: 12m, ownerApproved: true)),
        await client.PreviewLogAsync(logId),
        await client.PutLogTraceAsync(logId, new LogFields { Reason = TraceCorrectionTestKit.Reason }, ownerApproved: true),
        await client.PreviewLotAsync(t.LotId, new { laneNumbers = new[] { 1 }, finishedLaneNumbers = Array.Empty<int>(), cageCount = 9, reason = TraceCorrectionTestKit.Reason }),
        await factory.SendLotTraceAsync(client, t.LotId, HarvestFields.OfLanes(1) with { Cages = "9" }, ownerApproved: true),
    ];

    [Fact]
    public async Task An_accountant_and_a_zone_manager_of_another_zone_are_refused_everywhere_and_nothing_changes()
    {
        var t = await factory.TracedRaftAsync();
        var log = await t.Owner.Client.RecordLogAsync(t.CycleId);
        await t.Owner.Client.CloseCycleAsync(t.CycleId);
        var accountant = await factory.StaffAsync(null, StaffRole.Accountant);
        var otherZone = (await t.Owner.Client.CreateZoneAsync()).Id();
        var stranger = await factory.ManagerAsync(otherZone);

        foreach (var client in new[] { accountant.Client, stranger.Client })
        {
            foreach (var answer in await EveryCallAsync(client, t, log.Id()))
            {
                Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
                Assert.Equal("MSG-GEN-08", await answer.ErrorCodeAsync());
            }
        }

        Assert.Equal(10m, (await t.Owner.Client.GetSeedingAsync(t.SessionId)).GetProperty("quantity").GetDecimal());
        Assert.Equal(8, (await factory.LotRowAsync(t.LotId)).CageCount);
        Assert.Empty(await factory.AuditRowsAsync(TraceCorrectionTestKit.SeedingAction, t.SessionId));
    }

    [Fact]
    public async Task Unknown_records_answer_404()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var unknown = Guid.NewGuid();
        var answers = new[]
        {
            await owner.GetAsync($"/api/rafts/{unknown}/trace-corrections"),
            await owner.GetAsync($"/api/cultivation-cycles/{unknown}/trace-corrections"),
            await owner.PutSeedingTraceAsync(unknown, new { seedingDate = FarmingTestKit.Today, laneNumbers = new[] { 1 }, quantity = 1m, reason = "x", rowVersion = "" }),
            await owner.PreviewLogAsync(unknown),
            await owner.PreviewLotAsync(unknown, new { laneNumbers = new[] { 1 }, cageCount = 1, reason = "x" }),
        };

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.NotFound, a.StatusCode));
    }

    [Fact]
    public async Task The_screen_lists_the_cycles_of_the_raft_and_only_the_records_corrected_here()
    {
        var t = await factory.TracedRaftAsync();
        var log = await t.Owner.Client.RecordLogAsync(t.CycleId);

        // Open: only the session with a lane in a lot; the log and the lot are still corrected in their own screens.
        var open = await t.Manager.Client.TraceRecordsAsync(t.CycleId);
        var session = Assert.Single(open.GetProperty("seedingRecords").EnumerateArray());
        Assert.Equal((t.SessionId, t.LotCode), (session.Id(), Assert.Single(session.Strings("lotCodes"))));
        Assert.Empty(open.GetProperty("cultivationLogs").EnumerateArray());
        Assert.Empty(open.GetProperty("harvestLots").EnumerateArray());
        Assert.True(open.Flag("needsOwnerApproval"));
        Assert.True(open.IsNull("messageCode"));

        await t.Owner.Client.CloseCycleAsync(t.CycleId);
        var cycles = await t.Manager.Client.TraceCyclesAsync(t.RaftId);
        Assert.Equal((t.CycleId, "Closed"), (Assert.Single(cycles.GetProperty("cycles").EnumerateArray()).Id(), cycles.GetProperty("cycles")[0].Text("status")));
        var closed = await t.Owner.Client.TraceRecordsAsync(t.CycleId);
        Assert.Equal(log.Id(), Assert.Single(closed.GetProperty("cultivationLogs").EnumerateArray()).Id());
        var lot = Assert.Single(closed.GetProperty("harvestLots").EnumerateArray());
        Assert.Equal((t.LotCode, "NotPublished", 8), (lot.Text("lotCode"), lot.Text("originState"), lot.GetProperty("cageCount").GetInt32()));
        Assert.False(closed.Flag("needsOwnerApproval"));
    }

    [Fact]
    public async Task An_open_cycle_with_nothing_to_correct_here_answers_msg_gen_10()
    {
        var raft = await factory.SeededRaftAsync();

        var records = await raft.Owner.Client.TraceRecordsAsync(raft.CycleId);

        Assert.Empty(records.GetProperty("seedingRecords").EnumerateArray());
        Assert.Equal(("MSG-GEN-10", "There is no data for the cycle you chose. Please choose again."), (records.Text("messageCode"), records.Text("message")));
    }

    [Fact]
    public async Task A_discontinued_raft_keeps_its_records_read_only()
    {
        var t = await factory.TracedRaftAsync();
        await t.Owner.Client.CloseCycleAsync(t.CycleId);
        await factory.DiscontinueRaftAsync(t.RaftId);

        var refused = await (await t.Owner.Client.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(quantity: 12m))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", refused.Text("code"));
        Assert.Equal("MSG-GEN-09", await (await factory.SendLotTraceAsync(t.Owner.Client, t.LotId, HarvestFields.OfLanes(1) with { Cages = "9" })).ErrorCodeAsync());
        Assert.Single((await t.Owner.Client.TraceRecordsAsync(t.CycleId)).GetProperty("seedingRecords").EnumerateArray());
    }
}
