using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// BR-CYCLE-05 on the raft routes: a raft with no cultivation cycle, or one that is Discontinued, still gets care logs; the cycle is worked out from the visit date as for material usage.
/// </summary>
public class RaftCultivationLogTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static LogFields Edit(JsonElement log, DateOnly date) =>
        new() { VisitDate = date, Notes = "moved", Reason = "wrong date", KeepPhotoKeys = log.PhotoKeys(), Photos = [] };

    [Fact]
    public async Task A_raft_with_no_cycle_takes_a_log_that_has_no_cycle_and_lists_it_newest_visit_first()
    {
        var setup = await factory.RaftAsync();
        var owner = setup.Owner.Client;

        var older = await owner.RecordRaftLogAsync(setup.RaftId, new LogFields { VisitDate = Today.AddDays(-5), Notes = "older" });
        var newer = await owner.RecordRaftLogAsync(setup.RaftId, new LogFields { VisitDate = Today.AddDays(-1), Notes = "newer" });

        Assert.Equal(setup.RaftId, newer.GetProperty("raftId").GetGuid());
        Assert.Equal(JsonValueKind.Null, newer.GetProperty("cycleId").ValueKind);
        Assert.Equal(JsonValueKind.Null, newer.GetProperty("cycleStartDate").ValueKind);
        Assert.True(newer.Flag("canCorrect"));
        Assert.Equal(["newer", "older"], (await owner.ListRaftLogsAsync(setup.RaftId)).Select(l => l.Text("notes")).ToList());
        Assert.Equal(older.Id(), (await owner.GetLogAsync(older.Id())).Id());
    }

    [Fact]
    public async Task A_date_inside_the_open_cycle_belongs_to_it_and_a_date_before_it_has_none()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;

        var inside = await owner.RecordRaftLogAsync(setup.RaftId, new LogFields { VisitDate = Today.AddDays(-3) });
        var before = await owner.RecordRaftLogAsync(setup.RaftId, new LogFields { VisitDate = Today.AddDays(-20) });

        Assert.Equal(setup.CycleId, inside.GetProperty("cycleId").GetGuid());
        Assert.Equal(Today.AddDays(-10), inside.Date("cycleStartDate"));
        Assert.Equal(JsonValueKind.Null, before.GetProperty("cycleId").ValueKind);
        Assert.Equal(1, await factory.LogCountAsync(setup.CycleId));

        // The cycle list keeps only the logs of the cycle; the raft list has both.
        Assert.Single(await owner.ListLogsAsync(setup.CycleId));
        Assert.Equal(2, (await owner.ListRaftLogsAsync(setup.RaftId)).Count);
    }

    [Fact]
    public async Task A_date_inside_a_closed_cycle_is_refused_with_the_message_of_material_usage_and_nothing_is_saved()
    {
        var setup = await factory.RaftAsync();
        var owner = setup.Owner.Client;
        await factory.AddCycleAsync(setup.RaftId, Today.AddDays(-40), Today.AddDays(-30));

        var refused = await (await owner.PostRaftLogAsync(setup.RaftId, new LogFields { VisitDate = Today.AddDays(-35) })).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal("MSG-GEN-09", refused.Text("code"));
        Assert.Contains("is Closed and cannot be used for this action.", refused.Text("detail"));
        Assert.Empty(await owner.ListRaftLogsAsync(setup.RaftId));

        // After the close day the raft is free again.
        Assert.Equal(JsonValueKind.Null, (await owner.RecordRaftLogAsync(setup.RaftId, new LogFields { VisitDate = Today.AddDays(-29) })).GetProperty("cycleId").ValueKind);
    }

    [Fact]
    public async Task A_discontinued_raft_takes_logs_and_lets_them_be_corrected_and_removed_but_a_discontinued_zone_does_not()
    {
        var setup = await factory.RaftAsync();
        var owner = setup.Owner.Client;
        await factory.DiscontinueRaftAsync(setup.RaftId);

        var log = await owner.RecordRaftLogAsync(setup.RaftId, new LogFields { Notes = "repair of the frame", Photos = [] });
        Assert.True(log.Flag("canCorrect"));

        var corrected = await (await owner.PutLogAsync(log.Id(), new LogFields { Notes = "repair done", Reason = "typo", Photos = [] })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("repair done", corrected.GetProperty("log").Text("notes"));

        await factory.UpdateAsync<FarmingZone>(setup.ZoneId, z => z.StatusId = StatusIds.Usage.Discontinued);
        var refused = await (await owner.PostRaftLogAsync(setup.RaftId, new LogFields { Photos = [] })).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", refused.Text("code"));
        Assert.StartsWith("Farming zone ", refused.Text("detail"));
        Assert.False((await owner.GetLogAsync(log.Id())).Flag("canCorrect"));
        Assert.Equal("MSG-GEN-09", await (await owner.PutLogAsync(log.Id(), new LogFields { Reason = "no", Photos = [] })).ErrorCodeAsync());

        await factory.UpdateAsync<FarmingZone>(setup.ZoneId, z => z.StatusId = StatusIds.Usage.InUse);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.RemoveLogAsync(log.Id(), "recorded twice", null)).StatusCode);
        Assert.Empty(await owner.ListRaftLogsAsync(setup.RaftId));
    }

    [Fact]
    public async Task A_correction_works_the_cycle_out_from_the_new_date_and_a_log_in_a_closed_cycle_stays_locked()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var log = await owner.RecordRaftLogAsync(setup.RaftId, new LogFields { VisitDate = Today.AddDays(-3) });
        Assert.Equal(setup.CycleId, log.GetProperty("cycleId").GetGuid());

        var outside = await (await owner.PutLogAsync(log.Id(), Edit(log, Today.AddDays(-20)))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, outside.GetProperty("log").GetProperty("cycleId").ValueKind);

        var inside = await (await owner.PutLogAsync(log.Id(), Edit(log, Today.AddDays(-2)))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(setup.CycleId, inside.GetProperty("log").GetProperty("cycleId").GetGuid());

        await factory.CloseCycleAsync(setup.CycleId);
        Assert.False((await owner.GetLogAsync(log.Id())).Flag("canCorrect"));
        Assert.Equal("MSG-GEN-09", await (await owner.PutLogAsync(log.Id(), Edit(log, Today.AddDays(-2)))).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-09", await (await owner.RemoveLogAsync(log.Id(), "wrong raft", null)).ErrorCodeAsync());
    }

    [Fact]
    public async Task A_log_dated_after_today_is_invalid_and_an_unknown_raft_is_404()
    {
        var setup = await factory.RaftAsync();
        var owner = setup.Owner.Client;

        Assert.Equal(["Visit date"], await (await owner.PostRaftLogAsync(setup.RaftId, new LogFields { VisitDate = Today.AddDays(1) })).InvalidFieldsAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostRaftLogAsync(Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/rafts/{Guid.NewGuid()}/cultivation-logs")).StatusCode);
    }

    [Theory]
    [InlineData(StaffRole.Accountant, true)]
    [InlineData(StaffRole.ZoneManager, false)]
    public async Task An_accountant_and_a_zone_manager_of_another_zone_get_msg_gen_08_on_the_raft_routes(StaffRole role, bool allZones)
    {
        var setup = await factory.RaftAsync();
        var zone = role == StaffRole.Accountant && allZones
            ? null
            : await factory.ZoneEntityAsync((await setup.Owner.Client.CreateZoneAsync()).Id());
        var refused = await factory.StaffAsync(zone, role);

        var read = await refused.Client.GetAsync($"/api/rafts/{setup.RaftId}/cultivation-logs");
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal("MSG-GEN-08", await read.ErrorCodeAsync());
        var write = await refused.Client.PostRaftLogAsync(setup.RaftId);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal("MSG-GEN-08", await write.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/rafts/{setup.RaftId}/cultivation-logs")).StatusCode);
    }

    [Fact]
    public async Task The_zone_manager_of_the_zone_records_on_the_raft_and_corrects_only_their_own_log()
    {
        var setup = await factory.RaftAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var colleague = await factory.ManagerAsync(setup.ZoneId);

        var mine = await manager.Client.RecordRaftLogAsync(setup.RaftId);
        Assert.True(mine.Flag("canCorrect"));
        Assert.False(Assert.Single(await colleague.Client.ListRaftLogsAsync(setup.RaftId)).Flag("canCorrect"));
    }
}
