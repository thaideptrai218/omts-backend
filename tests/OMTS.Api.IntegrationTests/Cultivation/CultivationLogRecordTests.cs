using System.Net;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>CYCLE-04.0 and BR-CYCLE-05: a visit from the start date to today, with at least one photo, only while the cycle is Open.</summary>
public class CultivationLogRecordTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Category = "cultivation-log-photos";

    [Fact]
    public async Task A_zone_manager_records_a_visit_with_photos_and_notes()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var filesBefore = factory.StoredFiles(Category).Count;

        var answer = await (await manager.Client.PostLogAsync(setup.CycleId, new LogFields { VisitDate = Today.AddDays(-3), Notes = "  Hàu lớn đều  ", Photos = [Jpeg, Png] }))
            .ReadAsync(HttpStatusCode.Created);

        Assert.Equal(("Cultivation log saved.", "MSG-GEN-05"), (answer.Text("message"), answer.Text("messageCode")));
        var log = answer.GetProperty("log");
        Assert.Equal((setup.CycleId, setup.RaftId), (log.GetProperty("cycleId").GetGuid(), log.GetProperty("raftId").GetGuid()));
        Assert.Equal(Today.AddDays(-3), log.Date("visitDate"));
        Assert.Equal("Hàu lớn đều", log.Text("notes"));
        Assert.Equal([1, 2], log.PhotoSlots());
        Assert.All(log.PhotoKeys(), k => Assert.StartsWith($"{Category}/", k));
        Assert.Equal(manager.Account.Id, log.GetProperty("recordedByAccountId").GetGuid());
        Assert.Equal(manager.Account.DisplayName, log.Text("recordedByName"));
        Assert.True(log.Flag("canCorrect"));
        Assert.Equal(filesBefore + 2, factory.StoredFiles(Category).Count);

        var read = await manager.Client.GetLogAsync(log.Id());
        Assert.Equal(log.PhotoKeys(), read.PhotoKeys());
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(read.GetProperty("photos")[0].Text("url"))).StatusCode);

        var raftCode = log.Text("raftCode");
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "CultivationLog.Created" && e.RecordId == log.Id()));
        Assert.Equal((nameof(OMTS.Domain.Entities.Cultivation.CultivationLog), raftCode, setup.ZoneId, manager.Account.Id), (audit.RecordType, audit.RecordCode, audit.ZoneId, audit.ActorAccountId));
        Assert.Contains(log.PhotoKeys()[0], audit.After);
    }

    [Fact]
    public async Task The_visit_may_be_on_the_start_date_or_today_and_notes_are_optional_and_a_raft_may_have_many_logs_on_one_day()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;

        Assert.True((await owner.RecordLogAsync(setup.CycleId, new LogFields { VisitDate = Today.AddDays(-10), Notes = "   " })).IsNull("notes"));
        await owner.RecordLogAsync(setup.CycleId);
        await owner.RecordLogAsync(setup.CycleId);

        var logs = await owner.ListLogsAsync(setup.CycleId);
        Assert.Equal([Today, Today, Today.AddDays(-10)], logs.Select(l => l.Date("visitDate")));
    }

    public static TheoryData<string, LogFields> Refused => new()
    {
        { "Visit date", new LogFields { VisitDate = null } },
        { "Visit date", new LogFields { VisitDate = null, VisitDateText = "not a date" } },
        { "Visit date", new LogFields { VisitDate = null, VisitDateText = "01/02/2026" } },
        { "Visit date", new LogFields { VisitDate = Today.AddDays(-11) } },
        // Two days ahead: this list is built when the tests are discovered, and a run may cross midnight in UTC+7.
        { "Visit date", new LogFields { VisitDate = Today.AddDays(2) } },
        { "Notes", new LogFields { Notes = new string('x', 2001) } },
        // The photos of a care log are optional, at most three (owner decision of 12/11/2026).
        { "Photos", new LogFields { Photos = Enumerable.Repeat(Jpeg, 4).ToList() } },
        { "Photos", new LogFields { Photos = [Jpeg, TextPretendingToBeAFile] } },
        { "Photos", new LogFields { Photos = [Pdf] } },
        { "Photos", new LogFields { Photos = [Array.Empty<byte>()] } },
        { "Photos", new LogFields { Photos = [Sized(Jpeg, 10 * 1024 * 1024 + 1)] } },
        { "Photos", new LogFields { PhotoField = "photo" } },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task An_invalid_field_is_named_and_nothing_is_saved_or_stored(string field, LogFields fields)
    {
        var setup = await factory.CycleAsync();
        var filesBefore = factory.StoredFiles(Category).Count;

        var answer = await setup.Owner.Client.PostLogAsync(setup.CycleId, fields);

        Assert.Equal([field], await answer.InvalidFieldsAsync());
        Assert.Equal(0, await factory.LogCountAsync(setup.CycleId));
        Assert.Equal(filesBefore, factory.StoredFiles(Category).Count);
    }

    [Fact]
    public async Task Three_photos_or_none_are_allowed_and_every_invalid_field_is_listed_at_once()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;

        var three = await owner.RecordLogAsync(setup.CycleId, new LogFields { Photos = Enumerable.Repeat(Png, 3).ToList() });
        Assert.Equal(Enumerable.Range(1, 3), three.PhotoSlots());
        var none = await owner.RecordLogAsync(setup.CycleId, new LogFields { Photos = [] });
        Assert.Empty(none.PhotoSlots());

        var several = await owner.PostLogAsync(setup.CycleId, new LogFields { VisitDate = Today.AddDays(1), Notes = new string('x', 2001), Photos = [Pdf] });
        Assert.Equal(["Visit date", "Notes", "Photos"], await several.InvalidFieldsAsync());
    }

    [Fact]
    public async Task A_closed_cycle_or_a_discontinued_raft_refuses_a_new_log_with_msg_gen_09_and_an_unknown_cycle_is_404()
    {
        var closed = await factory.CycleAsync();
        await factory.CloseCycleAsync(closed.CycleId);
        var refused = await (await closed.Owner.Client.PostLogAsync(closed.CycleId)).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", refused.Text("code"));
        Assert.Contains("is Closed and cannot be used for this action.", refused.Text("detail"));

        var discontinued = await factory.CycleAsync();
        await factory.DiscontinueRaftAsync(discontinued.RaftId);
        var raftRefused = await (await discontinued.Owner.Client.PostLogAsync(discontinued.CycleId)).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", raftRefused.Text("code"));

        Assert.Equal(0, await factory.LogCountAsync(closed.CycleId) + await factory.LogCountAsync(discontinued.CycleId));
        Assert.Equal(HttpStatusCode.NotFound, (await closed.Owner.Client.PostLogAsync(Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await closed.Owner.Client.GetAsync($"/api/cultivation-logs/{Guid.NewGuid()}")).StatusCode);
    }
}
