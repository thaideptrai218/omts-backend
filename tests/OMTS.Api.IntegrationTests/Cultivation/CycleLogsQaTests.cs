using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cooperatives;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>QA of PR C4: the own-record rule, pairs, read-only states, the notification and the dates. A test that fails on purpose names its finding.</summary>
public class CycleLogsQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<LogFields> EditOfAsync(JsonElement log) => new()
    {
        Notes = "changed", Reason = "wrong note", RowVersion = await factory.LogVersionAsync(log.Id()), KeepPhotoKeys = log.PhotoKeys(), Photos = [],
    };

    private static async Task AssertForbiddenAsync(HttpResponseMessage answer)
    {
        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Equal("MSG-GEN-08", await answer.ErrorCodeAsync());
    }

    [Fact]
    public async Task Another_manager_of_the_zone_and_an_all_zones_manager_change_only_what_they_recorded()
    {
        var setup = await factory.CycleAsync();
        var (recorder, colleague, allZones) = (await factory.ManagerAsync(setup.ZoneId), await factory.ManagerAsync(setup.ZoneId), await factory.StaffAsync(null));
        var log = await recorder.Client.RecordLogAsync(setup.CycleId, new LogFields { Notes = "as recorded" });
        var ofAllZones = await allZones.Client.RecordLogAsync(setup.CycleId);
        var edit = await EditOfAsync(log);

        foreach (var other in new[] { colleague.Client, allZones.Client })
        {
            await AssertForbiddenAsync(await other.PutLogAsync(log.Id(), edit));
            await AssertForbiddenAsync(await other.RemoveLogAsync(log.Id(), "not mine", edit.RowVersion));
            Assert.False((await other.GetLogAsync(log.Id())).Flag("canCorrect"));
        }

        Assert.Equal("as recorded", (await setup.Owner.Client.GetLogAsync(log.Id())).Text("notes"));
        await (await allZones.Client.PutLogAsync(ofAllZones.Id(), await EditOfAsync(ofAllZones))).ReadAsync(HttpStatusCode.OK);
        await (await allZones.Client.RemoveLogAsync(ofAllZones.Id(), "wrong raft", await factory.LogVersionAsync(ofAllZones.Id()))).ReadAsync(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_manager_elsewhere_with_an_accountant_pair_here_and_on_all_zones_is_refused_on_every_route()
    {
        var setup = await factory.CycleAsync();
        var log = await setup.Owner.Client.RecordLogAsync(setup.CycleId);
        var person = await factory.ManagerAsync((await setup.Owner.Client.CreateZoneAsync()).Id());
        await factory.GrantAsync(person.Account, StaffRole.Accountant, await factory.ZoneEntityAsync(setup.ZoneId));
        await factory.GrantAsync(person.Account, StaffRole.Accountant, null);
        var client = person.Client;

        await AssertForbiddenAsync(await client.GetAsync($"/api/cultivation-cycles/{setup.CycleId}/cultivation-logs"));
        await AssertForbiddenAsync(await client.GetAsync($"/api/cultivation-logs/{log.Id()}"));
        await AssertForbiddenAsync(await client.PostLogAsync(setup.CycleId));
        await AssertForbiddenAsync(await client.PutLogAsync(log.Id(), await EditOfAsync(log)));
        await AssertForbiddenAsync(await client.RemoveLogAsync(log.Id(), "not mine", await factory.LogVersionAsync(log.Id())));
        await AssertForbiddenAsync(await client.GetAsync($"/api/rafts/{setup.RaftId}/progress"));
    }

    // GFR-02: a revoke applies from the next action, also on what the account recorded itself; a new pair gives the own logs back.
    [Fact]
    public async Task After_the_pair_is_revoked_the_recorder_is_refused_and_a_new_pair_gives_their_log_back()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var recorder = await factory.ManagerAsync(setup.ZoneId);
        var zoneB = await factory.ZoneEntityAsync((await owner.CreateZoneAsync()).Id());
        await factory.GrantAsync(recorder.Account, StaffRole.ZoneManager, zoneB);
        var log = await recorder.Client.RecordLogAsync(setup.CycleId);
        var edit = await EditOfAsync(log);

        var revoked = await owner.PostAsJsonAsync($"/api/accounts/{recorder.Account.Id}/grants/{recorder.Grant!.Id}/revoke", new { reason = "Moved" });
        Assert.True(revoked.IsSuccessStatusCode, await revoked.Content.ReadAsStringAsync());

        await AssertForbiddenAsync(await recorder.Client.GetAsync($"/api/cultivation-logs/{log.Id()}"));
        await AssertForbiddenAsync(await recorder.Client.GetAsync($"/api/cultivation-cycles/{setup.CycleId}/cultivation-logs"));
        await AssertForbiddenAsync(await recorder.Client.PostLogAsync(setup.CycleId));
        await AssertForbiddenAsync(await recorder.Client.PutLogAsync(log.Id(), edit));
        await AssertForbiddenAsync(await recorder.Client.RemoveLogAsync(log.Id(), "too late", edit.RowVersion));
        await AssertForbiddenAsync(await recorder.Client.GetAsync($"/api/rafts/{setup.RaftId}/progress"));
        Assert.Equal(recorder.Account.Id, (await owner.GetLogAsync(log.Id())).GetProperty("recordedByAccountId").GetGuid());

        await factory.GrantAsync(recorder.Account, StaffRole.ZoneManager, await factory.ZoneEntityAsync(setup.ZoneId));
        Assert.True((await recorder.Client.GetLogAsync(log.Id())).Flag("canCorrect"));
    }

    [Fact]
    public async Task The_progress_view_with_its_logs_is_closed_to_every_role_the_matrix_says_no_to()
    {
        var setup = await factory.CycleAsync();
        await setup.Owner.Client.RecordLogAsync(setup.CycleId);
        var zone = await factory.ZoneEntityAsync(setup.ZoneId);
        var other = await factory.ZoneEntityAsync((await setup.Owner.Client.CreateZoneAsync()).Id());
        var path = $"/api/rafts/{setup.RaftId}/progress";

        await AssertForbiddenAsync(await (await factory.StaffAsync(null, StaffRole.Accountant)).Client.GetAsync(path));
        await AssertForbiddenAsync(await (await factory.StaffAsync(zone, StaffRole.Accountant)).Client.GetAsync(path));
        await AssertForbiddenAsync(await (await factory.StaffAsync(other)).Client.GetAsync(path));
        var trader = await factory.TraderAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.GetAsync($"/api/cultivation-cycles/{setup.CycleId}/cultivation-logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(path)).StatusCode);
    }

    // GBR-01: read-only for everybody, Owner included, with every photo and field left as it was; the history can still be viewed.
    [Theory]
    [InlineData("raft", "Raft ")]
    [InlineData("zone", "Farming zone ")]
    public async Task A_discontinued_raft_or_zone_refuses_every_write_and_keeps_the_reads(string what, string named)
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var log = await manager.Client.RecordLogAsync(setup.CycleId, new LogFields { Notes = "kept" });
        var edit = await EditOfAsync(log);
        if (what == "raft")
        {
            await factory.DiscontinueRaftAsync(setup.RaftId);
        }
        else
        {
            await factory.UpdateAsync<FarmingZone>(setup.ZoneId, z => z.StatusId = StatusIds.Usage.Discontinued);
        }

        foreach (var client in new[] { manager.Client, setup.Owner.Client })
        {
            var added = await (await client.PostLogAsync(setup.CycleId, new LogFields { Photos = [] })).ReadAsync(HttpStatusCode.Conflict);
            Assert.Equal("MSG-GEN-09", added.Text("code"));
            Assert.StartsWith(named, added.Text("detail"));
            Assert.Equal("MSG-GEN-09", await (await client.PutLogAsync(log.Id(), edit)).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-09", await (await client.RemoveLogAsync(log.Id(), "wrong raft", edit.RowVersion)).ErrorCodeAsync());

            var read = await client.GetLogAsync(log.Id());
            Assert.Equal("kept", read.Text("notes"));
            Assert.False(read.Flag("canCorrect"));
            Assert.Single(await client.ListLogsAsync(setup.CycleId));
            var shown = (await client.ProgressAsync(setup.RaftId)).GetProperty("logs");
            Assert.False(Assert.Single(shown.EnumerateArray()).Flag("canCorrect"));
        }

        Assert.Equal(1, await factory.LogCountAsync(setup.CycleId));
        var url = (await setup.Owner.Client.GetLogAsync(log.Id())).GetProperty("photos")[0].Text("url");
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task A_closed_cycle_refuses_a_new_log_of_the_manager_and_the_progress_of_that_cycle_still_shows_its_logs()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var log = await manager.Client.RecordLogAsync(setup.CycleId);
        await factory.CloseCycleAsync(setup.CycleId);

        Assert.Equal("MSG-GEN-09", await (await manager.Client.PostLogAsync(setup.CycleId)).ErrorCodeAsync());
        var shown = (await manager.Client.ProgressAsync(setup.RaftId, $"?cycleId={setup.CycleId}")).GetProperty("logs");
        var only = Assert.Single(shown.EnumerateArray());
        Assert.Equal(log.Id(), only.Id());
        Assert.False(only.Flag("canCorrect"));
        Assert.Equal(1, await factory.LogCountAsync(setup.CycleId));
    }

    // MSG-CYCLE-04 has two placeholders, and a client composes its own language from them: nothing but these two values.
    [Fact]
    public async Task The_notification_holds_only_the_two_atomic_parameters_and_the_list_of_the_owner_shows_it_once()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var visit = Today.AddDays(-1);

        var log = await manager.Client.RecordLogAsync(setup.CycleId, new LogFields { VisitDate = visit });

        var row = await factory.QueryAsync(db => db.Notifications.SingleAsync(n => n.RelatedRecordId == log.Id() && n.RecipientAccountId == setup.Owner.Account.Id));
        using var parameters = JsonDocument.Parse(row.MessageParams!);
        Assert.Equal(["raft_code", "visit_date"], parameters.RootElement.EnumerateObject().Select(p => p.Name).Order());
        var list = await setup.Owner.Client.GetFromJsonAsync<JsonElement>("/api/me/notifications?pageSize=100");
        var items = list.GetProperty("items").EnumerateArray().Where(n => n.GetProperty("relatedRecordId").ValueKind != JsonValueKind.Null && n.GetProperty("relatedRecordId").GetGuid() == log.Id());
        Assert.Equal("MSG-CYCLE-04", Assert.Single(items).Text("messageCode"));
    }

    // ACCOUNT-08: opening looks at the access of today. Only Owners get this type, so rows are written by hand for the others.
    [Fact]
    public async Task Opening_the_notification_checks_the_zone_again_and_another_account_cannot_reach_it()
    {
        var setup = await factory.CycleAsync();
        var log = await setup.Owner.Client.RecordLogAsync(setup.CycleId);
        var (sameZone, otherZone, accountant) = (
            await factory.ManagerAsync(setup.ZoneId),
            await factory.ManagerAsync((await setup.Owner.Client.CreateZoneAsync()).Id()),
            await factory.StaffAsync(null, StaffRole.Accountant));

        async Task<HttpResponseMessage> OpenAsync(Actor actor)
        {
            var row = new Notification
            {
                RecipientAccountId = actor.Account.Id, Channel = NotificationChannel.InApp, Type = "CULTIVATION_LOG_RECORDED", Title = "New cultivation log",
                RelatedRecordType = "CultivationLog", RelatedRecordId = log.Id(), RelatedRecordCode = log.Text("raftCode"), CreatedAt = DateTimeOffset.UtcNow,
            };
            await factory.UseDbAsync(db => db.Notifications.Add(row));
            return await actor.Client.GetAsync($"/api/me/notifications/{row.Id}/open");
        }

        Assert.Equal(HttpStatusCode.OK, (await OpenAsync(sameZone)).StatusCode);
        await AssertForbiddenAsync(await OpenAsync(otherZone));
        await AssertForbiddenAsync(await OpenAsync(accountant));

        var ofOwner = new Notification
        {
            RecipientAccountId = setup.Owner.Account.Id, Channel = NotificationChannel.InApp, Type = "CULTIVATION_LOG_RECORDED", Title = "New cultivation log",
            RelatedRecordType = "CultivationLog", RelatedRecordId = log.Id(), CreatedAt = DateTimeOffset.UtcNow,
        };
        await factory.UseDbAsync(db => db.Notifications.Add(ofOwner));
        Assert.Equal(HttpStatusCode.NotFound, (await sameZone.Client.GetAsync($"/api/me/notifications/{ofOwner.Id}/open")).StatusCode);
    }

    // BR-CYCLE-05 follows the start date as corrected, and removing the first log frees it again (BR-CYCLE-01).
    [Fact]
    public async Task The_visit_date_follows_a_corrected_start_date_and_removing_the_first_log_frees_the_start_date()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var version = await factory.IssueRowVersionAsync<OMTS.Domain.Entities.Cultivation.CultivationCycle>(setup.CycleId);
        var cycle = await owner.EditCycleAsync(setup.CycleId, new CycleBody(Today.AddDays(-20), RowVersion: version));

        var early = await owner.RecordLogAsync(setup.CycleId, new LogFields { VisitDate = Today.AddDays(-20) });
        Assert.Equal(["Visit date"], await (await owner.PostLogAsync(setup.CycleId, new LogFields { VisitDate = Today.AddDays(-21) })).InvalidFieldsAsync());
        var later = new CycleBody(Today.AddDays(-10), RowVersion: cycle.Text("rowVersion"));
        Assert.Equal(["Start date"], await (await owner.PutCycleAsync(setup.CycleId, later)).InvalidFieldsAsync());

        await (await owner.RemoveLogAsync(early.Id(), "wrong day", await factory.LogVersionAsync(early.Id()))).ReadAsync(HttpStatusCode.NoContent);

        await owner.EditCycleAsync(setup.CycleId, later);
    }

    // BusinessClock: at 00:30 in Vietnam the UTC date is still yesterday, and "today" is the Vietnamese date.
    [Fact]
    public async Task Today_in_the_visit_rule_is_the_business_date_not_the_utc_date()
    {
        using var api = new MovableClockApiFactory();
        var setup = await api.CycleAsync();
        var instant = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero).AddHours(17.5);
        api.Clock.Set(instant);
        var localToday = BusinessClock.ToBusinessDate(instant);
        Assert.Equal(DateOnly.FromDateTime(instant.UtcDateTime).AddDays(1), localToday);

        await setup.Owner.Client.RecordLogAsync(setup.CycleId, new LogFields { VisitDate = localToday });
        Assert.Equal(["Visit date"], await (await setup.Owner.Client.PostLogAsync(setup.CycleId, new LogFields { VisitDate = localToday.AddDays(1) })).InvalidFieldsAsync());
    }

    // FINDING M1 (NFR-09: "Cultivation logs and water measurements have no version check (CYCLE-04, WATER-02)"; the design asks for a rowVersion, and the MSG06 row still cites old UC26: PM to settle). Fails on purpose: both answer 400 RowVersion.
    [Theory]
    [InlineData("correct")]
    [InlineData("remove")]
    public async Task M1_a_log_change_needs_no_version_as_nfr_09_says(string action)
    {
        var setup = await factory.CycleAsync();
        var log = await setup.Owner.Client.RecordLogAsync(setup.CycleId);

        var answer = action == "correct"
            ? await setup.Owner.Client.PutLogAsync(log.Id(), new LogFields { Notes = "changed", Reason = "wrong note", KeepPhotoKeys = log.PhotoKeys(), Photos = [] })
            : await setup.Owner.Client.RemoveLogAsync(log.Id(), "wrong raft", null);

        Assert.True(answer.IsSuccessStatusCode, $"{(int)answer.StatusCode}: {await answer.Content.ReadAsStringAsync()}");
    }

    // FINDING L2 (GFR-02, MSG-GEN-08 is the answer of a role that may not use the function). The controller's check of the part names runs before the service's access check,
    // so a role with no log rights gets the upload's own 400; the same request on a route whose checks all sit in the service answers 403. Fails on purpose: 400 Photos.
    [Theory]
    [InlineData(StaffRole.Accountant)]
    [InlineData(null)]
    public async Task L2_a_role_without_log_rights_gets_msg_gen_08_whatever_it_uploads(StaffRole? role)
    {
        var setup = await factory.CycleAsync();
        var client = role is { } staff ? (await factory.StaffAsync(null, staff)).Client : await factory.TraderAsync();

        var answer = await client.PostLogAsync(setup.CycleId, new LogFields { PhotoField = "photo" });

        Assert.True(answer.StatusCode == HttpStatusCode.Forbidden, $"{(int)answer.StatusCode}: {await answer.Content.ReadAsStringAsync()}");
    }

    // "notes left out is cleared": a change to null is a change, saved and audited, and only what changed is in the row.
    [Fact]
    public async Task Clearing_the_notes_or_moving_the_date_is_saved_and_audited_with_only_what_changed()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var log = await manager.Client.RecordLogAsync(setup.CycleId, new LogFields { Notes = "to clear" });
        var edit = await EditOfAsync(log) with { Notes = null };

        await (await manager.Client.PutLogAsync(log.Id(), edit)).ReadAsync(HttpStatusCode.OK);

        Assert.True((await manager.Client.GetLogAsync(log.Id())).IsNull("notes"));
        var cleared = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "CultivationLog.Corrected" && e.RecordId == log.Id()));
        Assert.Contains("to clear", cleared.Before);
        Assert.DoesNotContain("VisitDate", cleared.After);
        Assert.Equal(manager.Account.Id, cleared.ActorAccountId);

        await (await manager.Client.PutLogAsync(log.Id(), edit with { VisitDate = Today.AddDays(-3) })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(Today.AddDays(-3), (await manager.Client.GetLogAsync(log.Id())).Date("visitDate"));
        Assert.Equal(2, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "CultivationLog.Corrected" && e.RecordId == log.Id())));
    }

    [Fact]
    public void Ambient_actor_gives_the_outer_actor_back_and_ends_with_none()
    {
        var (actor, outer, inner) = (new AmbientActor(), Guid.NewGuid(), Guid.NewGuid());

        using (actor.Use(outer))
        {
            using (actor.Use(inner))
            {
                Assert.Equal(inner, actor.AccountId);
            }

            Assert.Equal(outer, actor.AccountId);
        }

        Assert.Null(actor.AccountId);
    }
}
