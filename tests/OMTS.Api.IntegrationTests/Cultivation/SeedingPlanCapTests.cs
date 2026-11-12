using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// Decision of 12/11/2026: the seed of a cycle (the sum of its sessions) against the planned seed quantity of the cycle, like the harvest cap. Up to 110 % it is saved; above it the caller is asked
/// (409 MSG-GEN-04) and answers with confirmOverPlan and a reason that the session and the audit log keep; above 150 % it is refused (400); no plan, no cap.
/// </summary>
public sealed class SeedingPlanCapTests
{
    private static Task<HttpResponseMessage> PostAsync(HttpClient owner, Guid cycleId, SeedLotMade lot, decimal quantity, bool? confirm = null, string? reason = null, int lane = 1) =>
        owner.PostAsync($"/api/cultivation-cycles/{cycleId}/seeding-records", StepFormKit.FormOf(new
        {
            seedingDate = FarmingTestKit.Today.AddDays(-1), seedLotId = lot.LineId, laneNumbers = new[] { lane }, quantity, confirmOverPlan = confirm ?? false, overPlanReason = reason,
        }, photos: 1));

    [Fact]
    public async Task Seed_within_110_percent_is_saved_above_it_asks_and_a_confirmed_session_keeps_its_reason_in_the_record_and_the_audit_log()
    {
        using var api = new AuthApiFactory();
        var setup = await api.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(1000m);
        await api.UpdateAsync<CultivationCycle>(setup.CycleId, c => c.ExpectedSeedQuantity = 100m);

        await (await PostAsync(owner, setup.CycleId, lot, 100m)).ReadAsync(HttpStatusCode.Created);
        await (await PostAsync(owner, setup.CycleId, lot, 10m, lane: 2)).ReadAsync(HttpStatusCode.Created); // 110 %: still fine

        var question = await (await PostAsync(owner, setup.CycleId, lot, 5m, lane: 3)).ReadAsync(HttpStatusCode.Conflict); // 115 %
        Assert.Equal(("MSG-GEN-04", "confirmOverPlan", "overPlanReason"), (question.Text("code"), question.Text("confirmFlag"), question.Text("reasonField")));
        Assert.Equal((100m, 110m, 5m, 115m, 15m), (question.Money("plannedSeedQuantity"), question.Money("seededQuantity"), question.Money("newQuantity"), question.Money("totalQuantity"), question.Money("overPlanPercent")));
        Assert.Equal(2, await api.QueryAsync(db => db.SeedingRecords.CountAsync()));

        Assert.Equal("Over plan reason", (await (await PostAsync(owner, setup.CycleId, lot, 5m, confirm: true, lane: 3)).ReadAsync(HttpStatusCode.BadRequest)).Text("field"));

        var saved = await (await PostAsync(owner, setup.CycleId, lot, 5m, confirm: true, reason: " Late order from the trader ", lane: 3)).ReadAsync(HttpStatusCode.Created);
        Assert.Equal("Late order from the trader", saved.GetProperty("record").Text("overPlanReason"));
        var audit = await api.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "SeedingRecord.Created" && e.After!.Contains("Late order from the trader")));
        Assert.Equal(setup.ZoneId, audit.ZoneId);

        var list = await owner.SeedingListAsync(setup.CycleId);
        Assert.Equal((100m, 115m, 115m), (list.Money("plannedSeedQuantity"), list.Money("seededQuantity"), list.Money("percent")));
        var summary = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{setup.CycleId}/summary");
        Assert.Equal((115m, 115m), (summary.Money("seedHung"), summary.Money("seedPercent")));
    }

    [Fact]
    public async Task Seed_above_150_percent_of_the_plan_is_refused_even_when_confirmed_and_a_cycle_without_a_plan_has_no_cap()
    {
        using var api = new AuthApiFactory();
        var setup = await api.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(1000m);

        await (await PostAsync(owner, setup.CycleId, lot, 500m)).ReadAsync(HttpStatusCode.Created); // no plan: no cap
        await api.UpdateAsync<CultivationCycle>(setup.CycleId, c => c.ExpectedSeedQuantity = 100m);

        var refused = await (await PostAsync(owner, setup.CycleId, lot, 100m, confirm: true, reason: "x", lane: 2)).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Quantity"), (refused.Text("code"), refused.Text("field")));
        Assert.Equal(1, await api.QueryAsync(db => db.SeedingRecords.CountAsync()));
    }

    [Fact]
    public async Task A_correction_is_judged_only_when_it_raises_the_seed_and_the_other_sessions_count_without_it()
    {
        using var api = new AuthApiFactory();
        var setup = await api.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(1000m);
        await api.UpdateAsync<CultivationCycle>(setup.CycleId, c => c.ExpectedSeedQuantity = 100m);
        var first = await owner.SeedingRecordAsync(setup.CycleId, lot.Session([1], quantity: 60m));
        await owner.SeedingRecordAsync(setup.CycleId, lot.Session([2], quantity: 40m));

        // 60 -> 70 makes 110 in all: fine. 60 -> 80 makes 120: asks, and the confirmed correction keeps the reason.
        var rowVersion = first.Text("rowVersion");
        var fine = await (await owner.PutSeedingAsync(first.Id(), first.AsCorrection() with { Quantity = 70m, RowVersion = rowVersion })).ReadAsync(HttpStatusCode.OK);
        var over = first.AsCorrection() with { Quantity = 80m, RowVersion = fine.GetProperty("record").Text("rowVersion") };
        var question = await (await owner.PutAsync($"/api/seeding-records/{first.Id()}", StepFormKit.FormOf(new { over.SeedingDate, seedLotId = over.SeedLotId, over.LaneNumbers, over.Quantity, over.Reason, over.RowVersion })))
            .ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-04", 120m), (question.Text("code"), question.Money("totalQuantity")));

        var confirmed = await (await owner.PutAsync($"/api/seeding-records/{first.Id()}", StepFormKit.FormOf(new
        {
            over.SeedingDate, seedLotId = over.SeedLotId, over.LaneNumbers, over.Quantity, over.Reason, over.RowVersion, confirmOverPlan = true, overPlanReason = "Re-weighed seed",
        }))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Re-weighed seed", confirmed.GetProperty("record").Text("overPlanReason"));

        // Lowering it again is never judged.
        var lower = first.AsCorrection() with { Quantity = 50m, RowVersion = confirmed.GetProperty("record").Text("rowVersion") };
        await (await owner.PutSeedingAsync(first.Id(), lower)).ReadAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_seed_lot_read_carries_what_is_on_hand_and_the_stock_refusal_names_the_balance()
    {
        using var api = new AuthApiFactory();
        var setup = await api.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(100m);
        var settings = await owner.GetFromJsonAsync<JsonElement>("/api/input-inventory-settings");
        await (await owner.PutAsJsonAsync("/api/input-inventory-settings", new { costMethod = "Fixed", allowNegativeQuantity = false, rowVersion = settings.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([1], quantity: 30m));

        var offered = (await owner.SeedLotsAsync(setup.RaftId)).GetProperty("lots").EnumerateArray().Single(l => l.Id() == lot.LineId);
        Assert.Equal(70m, offered.Money("onHand"));

        var refused = await (await PostAsync(owner, setup.CycleId, lot, 71m, lane: 2)).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-INPUT-01", 70m, -1m), (refused.Text("code"), refused.Money("onHand"), refused.Money("balanceAfter")));
    }
}

file static class SeedingPlanKit
{
    public static decimal Money(this JsonElement element, string property) => element.GetProperty(property).GetDecimal();
}
