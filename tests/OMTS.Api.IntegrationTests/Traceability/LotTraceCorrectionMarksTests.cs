using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>TRACE-02.0 step 3: the records corrected in Correct traceability data (TRACE-03) are marked with the date, the person and the reason; the trace shows the current data.</summary>
public class LotTraceCorrectionMarksTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static List<(Guid? Actor, string Reason, string At)> Marks(JsonElement record) => record.GetProperty("corrections").EnumerateArray()
        .Select(m => (m.GetProperty("actorAccountId").ValueKind == JsonValueKind.Null ? (Guid?)null : m.GetProperty("actorAccountId").GetGuid(), m.Text("reason"), m.Text("correctedAt")))
        .ToList();

    [Fact]
    public async Task A_corrected_session_and_a_corrected_lot_carry_their_marks()
    {
        var t = await factory.TracedRaftAsync();
        var before = await t.Manager.Client.TraceAsync(t.LotId);
        Assert.Empty(Marks(before));
        Assert.All(before.Seedings().Values, s => Assert.Empty(Marks(s)));

        // A Zone Manager corrects the session after the Owner agreed; then the cycle is closed and the Owner corrects the lot (BR-TRACE-04).
        var earlier = Today.AddDays(-2);
        (await t.Manager.Client.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(date: earlier, ownerApproved: true))).EnsureSuccessStatusCode();
        await t.Owner.Client.RecordLogAsync(t.CycleId, new LogFields { Notes = "Buoys fine" });
        await t.Owner.Client.CloseCycleAsync(t.CycleId);
        (await factory.SendLotTraceAsync(t.Owner.Client, t.LotId, HarvestFields.OfLanes(1) with { Cages = "10" }, reason: "Two cages were counted twice")).EnsureSuccessStatusCode();
        var sessionRow = Assert.Single(await factory.AuditRowsAsync(TraceCorrectionTestKit.SeedingAction, t.SessionId));
        var lotRow = Assert.Single(await factory.AuditRowsAsync(TraceCorrectionTestKit.LotAction, t.LotId));

        var trace = await t.Manager.Client.TraceAsync(t.LotId);

        var session = trace.Seedings()[t.SessionId];
        var sessionMark = Assert.Single(Marks(session));
        Assert.Equal(((Guid?)t.Manager.Account.Id, TraceCorrectionTestKit.Reason), (sessionMark.Actor, sessionMark.Reason));
        Assert.Equal(sessionRow.OccurredAt, DateTimeOffset.Parse(sessionMark.At));
        Assert.Equal(t.Manager.Account.DisplayName, session.GetProperty("corrections")[0].Text("actorName"));
        var lotMark = Assert.Single(Marks(trace));
        Assert.Equal(((Guid?)t.Owner.Account.Id, "Two cages were counted twice"), (lotMark.Actor, lotMark.Reason));
        Assert.Equal(lotRow.OccurredAt, DateTimeOffset.Parse(lotMark.At));

        // BR-TRACE-05: the trace shows the data as corrected, and the cycle is Closed.
        Assert.Equal((earlier, 10, "Closed"), (session.Date("seedingDate"), trace.GetProperty("cageCount").GetDecimal(), trace.Text("cycleStatus")));

        // TRACE-02.2: the marks are in the file too.
        var sheets = await (await t.Manager.Client.GetTraceExportAsync(t.LotId)).SheetsAsync();
        Assert.EndsWith("Two cages were counted twice", sheets["Lot"][1][17]);
        Assert.EndsWith(TraceCorrectionTestKit.Reason, sheets["Backward"][1][12]);
    }

    [Fact]
    public async Task A_correction_of_another_lot_or_session_is_not_marked_on_this_trace()
    {
        var t = await factory.TracedRaftAsync();
        var other = await factory.TracedRaftAsync();
        (await other.Owner.Client.PutSeedingTraceAsync(other.SessionId, other.Session.SeedingCorrection(date: Today.AddDays(-2)))).EnsureSuccessStatusCode();

        var trace = await t.Owner.Client.TraceAsync(t.LotId);

        Assert.Empty(Marks(trace));
        Assert.All(trace.Seedings().Values, s => Assert.Empty(Marks(s)));
        Assert.Equal(HttpStatusCode.OK, (await other.Owner.Client.GetTraceAsync(other.LotId)).StatusCode);
        Assert.Single(Marks((await other.Owner.Client.TraceAsync(other.LotId)).Seedings()[other.SessionId]));
    }
}
