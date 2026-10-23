using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Farming.Licenses;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>Review follow-ups of OMTS-210: drawing comparison, lanes in use, reminder candidates and catch-up, paging.</summary>
public class FarmingReviewFixTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly object VietnameseDrawing = new { label = "Bè ố số 1 – dãy “A”", shapes = new[] { new { kind = "rect", x = 1, y = 2 } } };

    // A notes-only edit of a raft whose drawing has Vietnamese text is not a drawing change.
    [Fact]
    public async Task Notes_edit_of_a_raft_with_a_vietnamese_drawing_records_no_drawing_change()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await owner.CreateZoneAsync();
        var raft = await owner.CreateRaftAsync(zone.Id(), NewRaftBody() with { LayoutDrawing = VietnameseDrawing });

        var notes = await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", NewRaftBody(raft.Text("code")) with
        {
            LayoutDrawing = VietnameseDrawing,
            ConditionNotes = "Dây neo mới",
            RowVersion = raft.Text("rowVersion"),
        });
        var afterNotes = await DrawingChangesAsync(raft.Id());
        var drawing = await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", NewRaftBody(raft.Text("code")) with
        {
            LayoutDrawing = new { label = "Bè ố số 2" },
            ConditionNotes = "Dây neo mới",
            RowVersion = (await notes.ReadAsync(HttpStatusCode.OK)).Text("rowVersion"),
        });

        Assert.Equal(0, afterNotes);
        Assert.Equal(HttpStatusCode.OK, drawing.StatusCode);
        Assert.Equal(1, await DrawingChangesAsync(raft.Id()));
        Assert.Equal("Bè ố số 2", (await drawing.ReadAsync(HttpStatusCode.OK)).GetProperty("layoutDrawing").GetProperty("label").GetString());
    }

    // A cultivation log entry keeps its lane: removing that lane is MSG11, not a 500 from the foreign key.
    [Fact]
    public async Task Lane_with_a_cultivation_log_entry_cannot_be_removed()
    {
        var actor = await factory.OwnerAsync();
        var zone = await actor.Client.CreateZoneAsync();
        var raft = await actor.Client.CreateRaftAsync(zone.Id(), NewRaftBody(lanes: 5));
        await factory.UseDbAsync(db => db.CultivationLogEntries.Add(new CultivationLogEntry
        {
            ZoneId = zone.Id(),
            LaneId = raft.LaneId(5),
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = "Observation",
            RecordedByAccountId = actor.Account.Id,
        }));

        var shrink = await actor.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", NewRaftBody(raft.Text("code"), lanes: 4) with { RowVersion = raft.Text("rowVersion") });
        var problem = await shrink.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, shrink.StatusCode);
        Assert.Equal("MSG11", problem.GetProperty("code").GetString());
        Assert.Equal([5], problem.GetProperty("laneNumbers").EnumerateArray().Select(n => n.GetInt32()));
    }

    // The reminder job loads only licenses with a notice due, never far-off or already-told ones.
    [Fact]
    public async Task Reminder_candidates_are_only_licenses_with_a_notice_due()
    {
        var today = Today;
        var due = License(today.AddDays(10));
        var far = License(today.AddDays(90));
        var told = License(today.AddDays(5), expiringSent: true);
        var expired = License(today.AddDays(-1), expiringSent: true);
        var expiredTold = License(today.AddDays(-3), expiringSent: true, expiredSent: true);
        var noExpiry = License(null);
        await factory.UseDbAsync(db => db.ZoneComplianceDocuments.AddRange(due, far, told, expired, expiredTold, noExpiry));

        using var scope = factory.Services.CreateScope();
        var ids = await scope.ServiceProvider.GetRequiredService<ZoneLicenseReminderService>().CandidateIdsAsync(today, default);

        Assert.Contains(due.Id, ids);
        Assert.Contains(expired.Id, ids);
        Assert.DoesNotContain(far.Id, ids);
        Assert.DoesNotContain(told.Id, ids);
        Assert.DoesNotContain(expiredTold.Id, ids);
        Assert.DoesNotContain(noExpiry.Id, ids);
    }

    // A host started after the daily run time (deploy, restart) runs once at start.
    [Theory]
    [InlineData("2026-10-23T00:30:00Z", true)]
    [InlineData("2026-10-22T23:59:00Z", false)]
    [InlineData("2026-10-22T16:59:00Z", true)]
    [InlineData("2026-10-23T00:00:00Z", true)]
    public void Reminder_catches_up_when_started_after_the_run_time(string utc, bool catchUp)
    {
        Assert.Equal(catchUp, ZoneLicenseReminderHostedService.IsPastRunTime(DateTimeOffset.Parse(utc), new TimeOnly(7, 0)));
    }

    [Fact]
    public async Task Huge_page_number_is_not_a_500()
    {
        var owner = (await factory.OwnerAsync()).Client;

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/zones?page={int.MaxValue}&pageSize=100")).StatusCode);
    }

    private Task<int> DrawingChangesAsync(Guid raftId) =>
        factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == raftId && e.Action == "Raft.LayoutDrawingChanged"));

    private static ZoneComplianceDocument License(DateOnly? expiry, bool expiringSent = false, bool expiredSent = false) => new()
    {
        DocumentType = "AquacultureLicense",
        DocumentNumber = $"GP-{NewCode()}",
        ExpiryDate = expiry,
        ExpiringReminderSentAt = expiringSent ? DateTimeOffset.UtcNow.AddDays(-1) : null,
        ExpiredReminderSentAt = expiredSent ? DateTimeOffset.UtcNow.AddDays(-1) : null,
    };
}
