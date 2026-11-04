using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Labor;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>CYCLE-01.1 Edit the start date or the plan estimates, and CYCLE-01.2 Cancel a cycle started by mistake.</summary>
public class CultivationCycleEditCancelTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<(HttpClient Owner, JsonElement Raft)> OwnerWithRaftAsync()
    {
        var owner = (await factory.OwnerAsync()).Client;
        return (owner, await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id()));
    }

    private Task AddSeedingAsync(Guid cycleId, DateOnly date) => factory.UseDbAsync(db => db.SeedingEvents.Add(new SeedingEvent
    {
        CycleId = cycleId, SeedLotId = Guid.NewGuid(), SeedingDate = date, RecordedByAccountId = Guid.NewGuid(),
    }));

    [Fact]
    public async Task An_edit_changes_the_plan_answers_the_message_and_audits_only_what_changed()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        var start = Today.AddDays(-5);
        var cycle = await owner.StartCycleAsync(raft.Id(), new CycleBody(start, 100m, null, 900m));

        var answer = await (await owner.PutCycleAsync(cycle.Id(), new CycleBody(start, 100m, Today.AddDays(30), null, cycle.Text("rowVersion")))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Cultivation cycle saved.", "MSG-GEN-05"), (answer.Text("message"), answer.Text("messageCode")));
        var saved = answer.GetProperty("cycle");
        Assert.Equal(Today.AddDays(30), saved.Date("expectedHarvestDate"));
        Assert.True(saved.IsNull("targetYieldKg")); // a plan estimate sent empty is cleared

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == cycle.Id() && e.Action == "CultivationCycle.Updated"));
        Assert.Contains("expectedHarvestDate", audit.After!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("targetYieldKg", audit.Before!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("startDate", audit.After!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_edit_that_changes_nothing_audits_nothing()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        var start = Today.AddDays(-5);
        var cycle = await owner.StartCycleAsync(raft.Id(), new CycleBody(start, 100m));

        await owner.EditCycleAsync(cycle.Id(), new CycleBody(start, 100m, RowVersion: cycle.Text("rowVersion")));

        Assert.False(await factory.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.RecordId == cycle.Id() && e.Action == "CultivationCycle.Updated")));
    }

    // BR-CYCLE-01: on an edit the start date is still not after today and after the previous close, and not after the first record.
    [Fact]
    public async Task The_start_date_of_an_edit_stays_within_today_the_previous_close_and_the_first_record()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        await factory.AddCycleAsync(raft.Id(), Today.AddDays(-40), Today.AddDays(-20));
        var cycle = await owner.StartCycleAsync(raft.Id(), new CycleBody(Today.AddDays(-10)));
        await AddSeedingAsync(cycle.Id(), Today.AddDays(-5));
        var version = cycle.Text("rowVersion");

        foreach (var day in new[] { 1, -4, -20, -30 })
        {
            Assert.Equal(["Start date"], await (await owner.PutCycleAsync(cycle.Id(), new CycleBody(Today.AddDays(day), RowVersion: version))).InvalidFieldsAsync());
        }

        Assert.Equal(["Start date"], await (await owner.PutCycleAsync(cycle.Id(), new CycleBody(null, RowVersion: version))).InvalidFieldsAsync());

        // On the first seeding day, and the day after the previous close.
        Assert.Equal(Today.AddDays(-5), (await owner.EditCycleAsync(cycle.Id(), new CycleBody(Today.AddDays(-5), RowVersion: version))).Date("startDate"));
        Assert.Equal(Today.AddDays(-19), (await owner.EditCycleAsync(cycle.Id(), new CycleBody(Today.AddDays(-19), RowVersion: version))).Date("startDate"));
    }

    // A new start date moves the rule of the expected harvest date with it.
    [Fact]
    public async Task The_expected_harvest_date_must_be_after_the_new_start_date()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        var cycle = await owner.StartCycleAsync(raft.Id(), new CycleBody(Today.AddDays(-5), null, Today.AddDays(-3)));

        var moved = await owner.PutCycleAsync(cycle.Id(), new CycleBody(Today.AddDays(-3), null, Today.AddDays(-3), RowVersion: cycle.Text("rowVersion")));

        Assert.Equal(["Expected harvest date"], await moved.InvalidFieldsAsync());
    }

    // BR-CYCLE-04: a Closed cycle is locked and never reopened.
    [Fact]
    public async Task A_closed_cycle_is_neither_edited_nor_cancelled()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        var closed = await factory.AddCycleAsync(raft.Id(), Today.AddDays(-20), Today.AddDays(-2));
        var version = (await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{closed.Id}")).Text("rowVersion");

        var edit = await (await owner.PutCycleAsync(closed.Id, new CycleBody(Today.AddDays(-19), RowVersion: version))).SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09", "MSG61");
        var cancel = await (await owner.CancelCycleAsync(closed.Id, version)).SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09", "MSG61");

        Assert.Equal($"The cultivation cycle of raft {raft.Text("code")} is Closed and cannot be used for this action.", edit.Detail());
        Assert.Equal(edit.Detail(), cancel.Detail());
        var kept = await factory.QueryAsync(db => db.CultivationCycles.SingleAsync(c => c.Id == closed.Id));
        Assert.Equal(Today.AddDays(-20), kept.StartDate);
    }

    [Fact]
    public async Task A_stale_row_version_is_refused_on_edit_and_cancel()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        var cycle = await owner.StartCycleAsync(raft.Id());
        var key = await factory.IssueRowVersionAsync<CultivationCycle>(cycle.Id());

        Assert.Equal("MSG06", await (await owner.PutCycleAsync(cycle.Id(), new CycleBody(Today, 5m, RowVersion: StaleRowVersion))).ErrorCodeAsync());
        Assert.Equal("MSG06", await (await owner.CancelCycleAsync(cycle.Id(), StaleRowVersion)).ErrorCodeAsync());
        Assert.Equal(["RowVersion"], await (await owner.CancelCycleAsync(cycle.Id(), null)).InvalidFieldsAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.CancelCycleAsync(cycle.Id(), key)).StatusCode);
    }

    // CYCLE-01.2: removed, kept only in the audit log, and the raft has no Open cycle again.
    [Fact]
    public async Task Cancelling_removes_the_cycle_keeps_it_in_the_audit_log_and_frees_the_raft()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        var start = Today.AddDays(-3);
        var cycle = await owner.StartCycleAsync(raft.Id(), new CycleBody(start, 42m));

        var answer = await owner.CancelCycleAsync(cycle.Id(), cycle.Text("rowVersion"));

        Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/cultivation-cycles/{cycle.Id()}")).StatusCode);
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == cycle.Id() && e.Action == "CultivationCycle.Cancelled"));
        Assert.Equal(raft.Text("code"), audit.RecordCode);
        Assert.Contains("42", audit.Before!);

        var next = await owner.StartCycleAsync(raft.Id());
        Assert.NotEqual(cycle.Id(), next.Id());
    }

    [Fact]
    public async Task A_cycle_with_a_seeding_session_cannot_be_cancelled()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        var cycle = await owner.StartCycleAsync(raft.Id());
        await AddSeedingAsync(cycle.Id(), Today);

        var refused = await (await owner.CancelCycleAsync(cycle.Id(), cycle.Text("rowVersion"))).SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09", "MSG61");

        Assert.Contains("in use by its records", refused.Detail());
        Assert.False((await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{cycle.Id()}")).Flag("canCancel"));
    }

    // The old work assignment still points to its cycle with a restricted key: a clean refusal, not a failed delete.
    [Fact]
    public async Task A_cycle_named_by_a_work_assignment_cannot_be_cancelled()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        var cycle = await owner.StartCycleAsync(raft.Id());
        var zoneId = raft.GetProperty("zone").Id();
        await factory.UseDbAsync(db => db.WorkAssignments.Add(new WorkAssignment { ZoneId = zoneId, CycleId = cycle.Id(), WorkDate = Today, Task = "Hang seed" }));

        var answer = await owner.CancelCycleAsync(cycle.Id(), cycle.Text("rowVersion"));

        Assert.Equal("MSG-GEN-09", await answer.ErrorCodeAsync());
    }
}
