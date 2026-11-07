using System.Net;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>CYCLE-04.1 and BR-CYCLE-05: a Zone Manager corrects or removes what they recorded, the Owner any log, with a reason, only while the cycle is Open.</summary>
public class CultivationLogCorrectionTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<LogFields> KeepAllAsync(System.Text.Json.JsonElement log) => new()
    {
        Reason = "wrong note", RowVersion = await factory.LogVersionAsync(log.Id()), KeepPhotoKeys = log.PhotoKeys(), Photos = [],
    };

    [Fact]
    public async Task A_zone_manager_corrects_only_their_own_log_and_the_owner_any()
    {
        var setup = await factory.CycleAsync();
        var (recorder, colleague) = (await factory.ManagerAsync(setup.ZoneId), await factory.ManagerAsync(setup.ZoneId));
        var ofRecorder = await recorder.Client.RecordLogAsync(setup.CycleId);
        var ofOwner = await setup.Owner.Client.RecordLogAsync(setup.CycleId);
        var edit = await KeepAllAsync(ofRecorder) with { Notes = "by the recorder" };

        // The flag says what the writes answer.
        Assert.True((await recorder.Client.GetLogAsync(ofRecorder.Id())).Flag("canCorrect"));
        Assert.False((await colleague.Client.GetLogAsync(ofRecorder.Id())).Flag("canCorrect"));
        Assert.False((await recorder.Client.GetLogAsync(ofOwner.Id())).Flag("canCorrect"));
        Assert.True((await setup.Owner.Client.GetLogAsync(ofRecorder.Id())).Flag("canCorrect"));

        var refused = await colleague.Client.PutLogAsync(ofRecorder.Id(), edit);
        Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (refused.StatusCode, await refused.ErrorCodeAsync()));
        Assert.Equal(HttpStatusCode.Forbidden, (await colleague.Client.RemoveLogAsync(ofRecorder.Id(), "not mine", edit.RowVersion)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await recorder.Client.PutLogAsync(ofOwner.Id(), edit)).StatusCode);

        var saved = await (await recorder.Client.PutLogAsync(ofRecorder.Id(), edit)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("by the recorder", "MSG-GEN-05"), (saved.GetProperty("log").Text("notes"), saved.Text("messageCode")));

        var byOwner = await (await setup.Owner.Client.PutLogAsync(ofRecorder.Id(), edit with { Notes = "by the owner" })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("by the owner", byOwner.GetProperty("log").Text("notes"));

        // The recorder stays who first recorded it, so they can still correct it.
        Assert.Equal(recorder.Account.Id, byOwner.GetProperty("log").GetProperty("recordedByAccountId").GetGuid());
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Owner.Client.RemoveLogAsync(ofRecorder.Id(), "wrong raft", edit.RowVersion)).StatusCode);
    }

    [Fact]
    public async Task A_closed_cycle_refuses_a_correction_and_a_removal_also_for_the_owner_and_keeps_its_logs_readable()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var log = await manager.Client.RecordLogAsync(setup.CycleId);
        var edit = await KeepAllAsync(log) with { Notes = "too late" };
        await factory.CloseCycleAsync(setup.CycleId);

        foreach (var client in new[] { manager.Client, setup.Owner.Client })
        {
            Assert.Equal("MSG-GEN-09", await (await client.PutLogAsync(log.Id(), edit)).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-09", await (await client.RemoveLogAsync(log.Id(), "too late", edit.RowVersion)).ErrorCodeAsync());
            var read = Assert.Single(await client.ListLogsAsync(setup.CycleId));
            Assert.False(read.Flag("canCorrect"));
            Assert.Equal(JsonNull, read.GetProperty("notes").ValueKind);
        }
    }

    private const System.Text.Json.JsonValueKind JsonNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task The_fields_are_checked_as_on_a_new_log_and_the_reason_is_required()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var log = await owner.RecordLogAsync(setup.CycleId);
        var edit = await KeepAllAsync(log);

        Assert.Equal(["Reason"], await (await owner.PutLogAsync(log.Id(), edit with { Reason = null })).InvalidFieldsAsync());
        Assert.Equal(["Reason"], await (await owner.PutLogAsync(log.Id(), edit with { Reason = "   " })).InvalidFieldsAsync());
        Assert.Equal(["Reason"], await (await owner.PutLogAsync(log.Id(), edit with { Reason = new string('x', 1001) })).InvalidFieldsAsync());
        Assert.Equal(["Visit date"], await (await owner.PutLogAsync(log.Id(), edit with { VisitDate = Today.AddDays(-11) })).InvalidFieldsAsync());
        Assert.Equal(["Visit date"], await (await owner.PutLogAsync(log.Id(), edit with { VisitDate = Today.AddDays(1) })).InvalidFieldsAsync());
        Assert.Equal(["Notes"], await (await owner.PutLogAsync(log.Id(), edit with { Notes = new string('x', 2001) })).InvalidFieldsAsync());
        Assert.Equal(["Reason"], await (await owner.RemoveLogAsync(log.Id(), " ", edit.RowVersion)).InvalidFieldsAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutLogAsync(Guid.NewGuid(), edit)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.RemoveLogAsync(Guid.NewGuid(), "wrong raft", edit.RowVersion)).StatusCode);

        var read = await owner.GetLogAsync(log.Id());
        Assert.Equal(log.PhotoKeys(), read.PhotoKeys());
        Assert.Equal(Today, read.Date("visitDate"));
    }

    [Fact]
    public async Task Photos_not_kept_are_removed_kept_ones_stay_in_their_slots_and_new_ones_take_the_free_slots()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var log = await owner.RecordLogAsync(setup.CycleId, new LogFields { Photos = [Jpeg, Png, Jpeg] });
        var keys = log.PhotoKeys();
        var edit = await KeepAllAsync(log);

        var saved = (await (await owner.PutLogAsync(log.Id(), edit with { KeepPhotoKeys = [keys[1], keys[1]], Photos = [Png] })).ReadAsync(HttpStatusCode.OK)).GetProperty("log");

        Assert.Equal([1, 2], saved.PhotoSlots());
        Assert.Equal(keys[1], saved.PhotoKeys()[1]);
        Assert.DoesNotContain(keys[0], saved.PhotoKeys());
        Assert.DoesNotContain(keys[2], saved.PhotoKeys());

        // All replaced at once: slot 1 is taken over in one save.
        var replaced = (await (await owner.PutLogAsync(log.Id(), edit with { KeepPhotoKeys = [], Photos = [Jpeg] })).ReadAsync(HttpStatusCode.OK)).GetProperty("log");
        Assert.Equal([1], replaced.PhotoSlots());
        Assert.DoesNotContain(replaced.PhotoKeys()[0], saved.PhotoKeys());
    }

    // Keys come from the client: only the exact keys of this log are kept, never a photo of another log, and never a file by another spelling of its key.
    [Fact]
    public async Task The_kept_photos_must_be_of_this_log_spelled_as_stored_and_at_least_one_and_at_most_ten_stay()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var log = await owner.RecordLogAsync(setup.CycleId, new LogFields { Photos = Enumerable.Repeat(Jpeg, 9).ToList() });
        var other = await owner.RecordLogAsync(setup.CycleId);
        var keys = log.PhotoKeys();
        var edit = await KeepAllAsync(log);

        foreach (var wrong in new[] { other.PhotoKeys()[0], keys[0].ToUpperInvariant(), "cultivation-log-photos/2026/01/" + new string('a', 32) + ".jpg", "raft-photos/../x" })
        {
            Assert.Equal(["Photos"], await (await owner.PutLogAsync(log.Id(), edit with { KeepPhotoKeys = [.. keys, wrong] })).InvalidFieldsAsync());
        }

        Assert.Equal(["Photos"], await (await owner.PutLogAsync(log.Id(), edit with { KeepPhotoKeys = [] })).InvalidFieldsAsync());
        Assert.Equal(["Photos"], await (await owner.PutLogAsync(log.Id(), edit with { Photos = [Png, Png] })).InvalidFieldsAsync());
        Assert.Equal(["Photos"], await (await owner.PutLogAsync(log.Id(), edit with { Photos = [TextPretendingToBeAFile] })).InvalidFieldsAsync());
        Assert.Equal(keys, (await owner.GetLogAsync(log.Id())).PhotoKeys());
        Assert.Equal(other.PhotoKeys(), (await owner.GetLogAsync(other.Id())).PhotoKeys());

        var ten = (await (await owner.PutLogAsync(log.Id(), edit with { Photos = [Png] })).ReadAsync(HttpStatusCode.OK)).GetProperty("log");
        Assert.Equal(Enumerable.Range(1, 10), ten.PhotoSlots());
    }

    [Fact]
    public async Task A_correction_is_audited_with_its_reason_and_one_that_changes_nothing_writes_no_row()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var log = await owner.RecordLogAsync(setup.CycleId, new LogFields { Notes = "before" });
        var edit = await KeepAllAsync(log) with { Notes = "before" };

        await (await owner.PutLogAsync(log.Id(), edit)).ReadAsync(HttpStatusCode.OK);
        Assert.False(await factory.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "CultivationLog.Corrected" && e.RecordId == log.Id())));

        await (await owner.PutLogAsync(log.Id(), edit with { Notes = "after", Reason = "  wrong note " })).ReadAsync(HttpStatusCode.OK);
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "CultivationLog.Corrected" && e.RecordId == log.Id()));
        Assert.Equal("wrong note", audit.Reason);
        Assert.Contains("before", audit.Before);
        Assert.Contains("after", audit.After);
        Assert.DoesNotContain("Photos", audit.After);
    }

    [Fact]
    public async Task A_removed_log_is_gone_with_its_files_and_stays_in_the_audit_log()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var log = await owner.RecordLogAsync(setup.CycleId, new LogFields { Photos = [Jpeg, Png], Notes = "gone" });

        await (await owner.RemoveLogAsync(log.Id(), " wrong raft ", "")).ReadAsync(HttpStatusCode.NoContent);

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/cultivation-logs/{log.Id()}")).StatusCode);
        Assert.Empty(await owner.ListLogsAsync(setup.CycleId));
        Assert.False(await factory.QueryAsync(db => db.CultivationLogPhotos.AnyAsync(p => p.LogId == log.Id())));
        var stored = factory.StoredFiles("cultivation-log-photos").Select(f => Path.GetRelativePath(factory.StorageRoot, f).Replace('\\', '/'));
        Assert.Empty(stored.Intersect(log.PhotoKeys()));
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "CultivationLog.Removed" && e.RecordId == log.Id()));
        Assert.Equal("wrong raft", audit.Reason);
        Assert.Contains(log.PhotoKeys()[1], audit.Before);
    }
}
