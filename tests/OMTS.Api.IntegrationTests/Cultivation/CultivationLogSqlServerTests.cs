using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Cultivation;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>CYCLE-04 on SQL Server: real row versions, the constraints, and parallel changes of one cycle or log. No race may end in a 500.</summary>
public class CultivationLogSqlServerTests
{
    private const string Category = FileCategories.CultivationLogPhotos;

    private static async Task<(FarmingSqlServerHost Host, HttpClient Owner, Guid RaftId)> StartAsync()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        return (host, owner, (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id());
    }

    private static Task<JsonElement> CycleAsync(HttpClient client, Guid cycleId) => client.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{cycleId}");

    private static Task<List<string>> KeysInDatabaseAsync(FarmingSqlServerHost host) =>
        host.QueryAsync(db => db.CultivationLogPhotos.Select(p => p.FileKey).OrderBy(k => k).ToListAsync());

    /// <summary>Every answer is one of the expected statuses, a 409 is MSG06; shows them all when not.</summary>
    private static async Task AssertAnsweredAsync(IEnumerable<HttpResponseMessage> answers, params HttpStatusCode[] successes)
    {
        foreach (var answer in answers)
        {
            var text = await answer.Content.ReadAsStringAsync();
            if (answer.StatusCode == HttpStatusCode.Conflict)
            {
                Assert.Contains("\"code\":\"MSG", text.Replace(" ", ""));
            }
            else
            {
                Assert.True(successes.Contains(answer.StatusCode), $"{(int)answer.StatusCode}: {text}");
            }
        }
    }

    [SqlServerFact]
    public async Task Each_write_moves_the_row_versions_also_a_change_of_photos_only_and_a_slot_can_be_taken_over_in_one_save()
    {
        var (host, owner, raftId) = await StartAsync();
        await using var _ = host;
        var started = await owner.StartCycleAsync(raftId);

        var log = await owner.RecordLogAsync(started.Id(), new LogFields { Photos = [Jpeg, Png] });
        Assert.NotEqual(started.Text("rowVersion"), (await CycleAsync(owner, started.Id())).Text("rowVersion"));
        Assert.Equal(8, Convert.FromBase64String(log.Text("rowVersion")).Length);

        // No field of the log changes, only its photos: the version moves, so a tab open on the old photos fails.
        var edit = new LogFields { Reason = "a better photo", RowVersion = log.Text("rowVersion"), KeepPhotoKeys = [log.PhotoKeys()[1]] };
        var cycleBefore = (await CycleAsync(owner, started.Id())).Text("rowVersion");
        var saved = (await (await owner.PutLogAsync(log.Id(), edit)).ReadAsync(HttpStatusCode.OK)).GetProperty("log");
        Assert.NotEqual(log.Text("rowVersion"), saved.Text("rowVersion"));
        Assert.NotEqual(cycleBefore, (await CycleAsync(owner, started.Id())).Text("rowVersion"));
        Assert.Equal([1, 2], saved.PhotoSlots());
        Assert.Equal("MSG06", await (await owner.PutLogAsync(log.Id(), edit)).ErrorCodeAsync());

        // Nothing changes: no new version, of the log or the cycle.
        var unchanged = saved.Text("rowVersion");
        var cycleNow = (await CycleAsync(owner, started.Id())).Text("rowVersion");
        var same = new LogFields { Reason = "no change", RowVersion = unchanged, KeepPhotoKeys = saved.PhotoKeys(), Photos = [] };
        Assert.Equal(unchanged, (await (await owner.PutLogAsync(log.Id(), same)).ReadAsync(HttpStatusCode.OK)).GetProperty("log").Text("rowVersion"));
        Assert.Equal(cycleNow, (await CycleAsync(owner, started.Id())).Text("rowVersion"));

        // Every photo goes and slot 1 is used again in the same save.
        var replaced = (await (await owner.PutLogAsync(log.Id(), same with { KeepPhotoKeys = [], Photos = [Jpeg] })).ReadAsync(HttpStatusCode.OK)).GetProperty("log");
        Assert.Equal([1], replaced.PhotoSlots());
        Assert.Equal(replaced.PhotoKeys(), host.StoredKeys(Category));

        await (await owner.RemoveLogAsync(log.Id(), "wrong raft", replaced.Text("rowVersion"))).ReadAsync(HttpStatusCode.NoContent);
        Assert.Empty(await KeysInDatabaseAsync(host));
        Assert.Empty(host.StoredKeys(Category));
    }

    [SqlServerFact]
    public async Task The_database_refuses_a_slot_out_of_range_or_taken_and_a_cycle_with_a_log_cannot_be_deleted()
    {
        var (host, owner, raftId) = await StartAsync();
        await using var _ = host;
        var cycle = await owner.StartCycleAsync(raftId);
        var log = await owner.RecordLogAsync(cycle.Id());

        CultivationLogPhoto Photo(int slot) => new() { LogId = log.Id(), SortOrder = slot, FileKey = "x" };
        var outOfRange = await Assert.ThrowsAsync<DbUpdateException>(() => host.UseDbAsync(db => db.CultivationLogPhotos.Add(Photo(11))));
        Assert.Contains("CK_CultivationLogPhoto_SortOrder", outOfRange.InnerException!.Message);
        var taken = await Assert.ThrowsAsync<DbUpdateException>(() => host.UseDbAsync(db => db.CultivationLogPhotos.Add(Photo(1))));
        Assert.Contains("IX_CultivationLogPhotos_LogId_SortOrder", taken.InnerException!.Message);
        var inUse = await Assert.ThrowsAsync<SqlException>(() => host.QueryAsync(db => db.CultivationCycles.Where(c => c.Id == cycle.Id()).ExecuteDeleteAsync()));
        Assert.Contains("FK_CultivationLogs_CultivationCycles_CycleId", inUse.Message);
    }

    // A log and a cancel of one cycle: the cycle row decides. Never a log on no cycle, a cancelled cycle with a file left, or a 500.
    [SqlServerFact]
    public async Task A_log_racing_a_cancel_never_answers_500_and_leaves_one_consistent_result()
    {
        var (host, owner, _) = await StartAsync();
        await using var _host = host;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        for (var round = 0; round < 12; round++)
        {
            var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
            var cycle = await owner.StartCycleAsync(raftId);

            var answers = await Task.WhenAll(owner.PostLogAsync(cycle.Id()), owner.CancelCycleAsync(cycle.Id(), cycle.Text("rowVersion")));

            // A log that starts after the cancel finished finds no cycle: 404.
            await AssertAnsweredAsync(answers, HttpStatusCode.Created, HttpStatusCode.NoContent, HttpStatusCode.NotFound);
            var logged = answers[0].StatusCode == HttpStatusCode.Created;
            var cancelled = answers[1].StatusCode == HttpStatusCode.NoContent;
            Assert.False(logged && cancelled, "A log was saved on a cancelled cycle.");
            Assert.Equal(!cancelled, await host.QueryAsync(db => db.CultivationCycles.AnyAsync(c => c.Id == cycle.Id())));
            Assert.Equal(logged ? 1 : 0, await host.QueryAsync(db => db.CultivationLogs.CountAsync(l => l.CycleId == cycle.Id())));
        }

        Assert.Equal(await KeysInDatabaseAsync(host), host.StoredKeys(Category));
    }

    // BR-CYCLE-01 holds in a race: a start date after the first log can never be saved.
    [SqlServerFact]
    public async Task A_new_start_date_racing_an_earlier_log_never_leaves_the_log_before_the_start()
    {
        var (host, owner, _) = await StartAsync();
        await using var _host = host;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        for (var round = 0; round < 8; round++)
        {
            var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
            var cycle = await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)));
            var moved = new CycleBody(Today, RowVersion: cycle.Text("rowVersion"));

            var answers = await Task.WhenAll(owner.PostLogAsync(cycle.Id(), new LogFields { VisitDate = Today.AddDays(-5) }), owner.PutCycleAsync(cycle.Id(), moved));

            foreach (var answer in answers)
            {
                Assert.True((int)answer.StatusCode < 500, await answer.Content.ReadAsStringAsync());
            }

            var start = (await CycleAsync(owner, cycle.Id())).Date("startDate");
            var logs = await owner.ListLogsAsync(cycle.Id());
            Assert.All(logs, l => Assert.True(l.Date("visitDate") >= start));
        }
    }

    [SqlServerFact]
    public async Task Parallel_corrections_of_one_log_let_one_win_and_every_stored_file_belongs_to_a_row()
    {
        var (host, owner, raftId) = await StartAsync();
        await using var _ = host;
        var cycle = await owner.StartCycleAsync(raftId);
        var log = await owner.RecordLogAsync(cycle.Id(), new LogFields { Photos = [Jpeg, Png] });

        var answers = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => owner.PutLogAsync(log.Id(), new LogFields
        {
            Notes = $"try {i}", Reason = "parallel", RowVersion = log.Text("rowVersion"), KeepPhotoKeys = [log.PhotoKeys()[0]], Photos = [Png, Jpeg],
        })));

        await AssertAnsweredAsync(answers, HttpStatusCode.OK);
        Assert.Equal(1, answers.Count(a => a.StatusCode == HttpStatusCode.OK));
        var read = await owner.GetLogAsync(log.Id());
        Assert.Equal([1, 2, 3], read.PhotoSlots());
        Assert.Equal(await KeysInDatabaseAsync(host), host.StoredKeys(Category));
    }

    [SqlServerFact]
    public async Task Parallel_logs_of_one_cycle_each_save_whole_or_not_at_all_with_one_notification_per_saved_log()
    {
        var (host, owner, raftId) = await StartAsync();
        await using var _ = host;
        var zoneId = await host.QueryAsync(db => db.Rafts.Where(r => r.Id == raftId).Select(r => r.ZoneId).SingleAsync());
        var manager = await host.StaffInZoneAsync(zoneId);
        var cycle = await owner.StartCycleAsync(raftId);

        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => manager.Client.PostLogAsync(cycle.Id(), new LogFields { Photos = [Jpeg, Png] })));

        await AssertAnsweredAsync(answers, HttpStatusCode.Created);
        var saved = answers.Count(a => a.StatusCode == HttpStatusCode.Created);
        Assert.True(saved >= 1);
        Assert.Equal(saved, await host.QueryAsync(db => db.CultivationLogs.CountAsync(l => l.CycleId == cycle.Id())));
        Assert.Equal(saved, await host.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == "CULTIVATION_LOG_RECORDED" && n.RecipientAccountId == host.Owner.Account.Id)));
        Assert.Equal(saved * 2, (await KeysInDatabaseAsync(host)).Count);
        Assert.Equal(await KeysInDatabaseAsync(host), host.StoredKeys(Category));
    }
}
