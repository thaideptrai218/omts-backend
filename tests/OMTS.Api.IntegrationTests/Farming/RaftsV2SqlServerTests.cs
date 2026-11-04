using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>FARM-04 on SQL Server: real row versions, the lane and photo indexes, and parallel changes of one raft.</summary>
public class RaftsV2SqlServerTests
{
    // The row version moves on a lane change, though only Lane rows differ, so a second editor gets MSG06.
    [SqlServerFact]
    public async Task Lane_change_moves_the_row_version_and_a_stale_tab_is_refused()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var body = NewRaftBody(lanes: 3);
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id(), body);

        var fewer = await owner.EditRaftAsync(raft.Id(), body with { LaneCount = 2, RowVersion = raft.Text("rowVersion") });
        Assert.NotEqual(raft.Text("rowVersion"), fewer.Text("rowVersion"));
        var stale = await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with { LaneCount = 4, RowVersion = raft.Text("rowVersion") });
        Assert.Equal("MSG06", await stale.ErrorCodeAsync());

        var kept = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}");
        Assert.Equal([1, 2], kept.LaneNumbers(StatusIds.Lane.InUse));
        Assert.Equal([3], kept.LaneNumbers(StatusIds.Lane.Removed));

        // Saving an unchanged raft changes nothing: same row version, no audit row.
        var same = await owner.EditRaftAsync(raft.Id(), body with { LaneCount = 2, RowVersion = fewer.Text("rowVersion") });
        Assert.Equal(fewer.Text("rowVersion"), same.Text("rowVersion"));
    }

    // Parallel grows from one row version: exactly one wins, the numbers stay 1..N without a gap or a repeat.
    [SqlServerFact]
    public async Task Parallel_lane_changes_leave_one_winner_and_gapless_numbers()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var body = NewRaftBody(lanes: 2);
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id(), body);

        var answers = await Task.WhenAll(new[] { 4, 5, 6 }.Select(count =>
            owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with { LaneCount = count, RowVersion = raft.Text("rowVersion") })));

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        foreach (var lost in answers.Where(a => a.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal((HttpStatusCode.Conflict, "MSG06"), (lost.StatusCode, await lost.ErrorCodeAsync()));
        }

        var numbers = await host.QueryAsync(db => db.Lanes.Where(l => l.RaftId == raft.Id()).OrderBy(l => l.LaneNumber).Select(l => l.LaneNumber).ToListAsync());
        Assert.Equal(Enumerable.Range(1, numbers.Count), numbers);
    }

    // Parallel uploads can never leave more than three photos, and no answer is anything but saved, conflict or refused.
    [SqlServerFact]
    public async Task Parallel_uploads_never_leave_more_than_three_photos_or_an_orphan_of_a_lost_save()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var raftId = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id();

        for (var round = 0; round < 3; round++)
        {
            var answers = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => owner.UploadRaftPhotoAsync(raftId)));
            foreach (var answer in answers)
            {
                Assert.True(answer.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.BadRequest, $"round {round}: {(int)answer.StatusCode} {await answer.Content.ReadAsStringAsync()}");
            }
        }

        var keys = await host.QueryAsync(db => db.RaftPhotos.Where(p => p.RaftId == raftId).Select(p => p.FileKey).ToListAsync());
        Assert.InRange(keys.Count, 1, Raft.MaxPhotos);
        Assert.Equal(keys.Order(), host.StoredKeys("raft-photos"));
    }

    // The database itself refuses what the service checks first: a repeated lane number, a fourth slot, a slot taken twice.
    [SqlServerFact]
    public async Task The_database_refuses_a_repeated_lane_number_and_photo_slots_out_of_range_or_taken()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var raftId = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id(), NewRaftBody(lanes: 1))).Id();
        await owner.AddRaftPhotoAsync(raftId);

        var lane = await Assert.ThrowsAsync<DbUpdateException>(() => host.UseDbAsync(db => db.Lanes.Add(new Lane { RaftId = raftId, LaneNumber = 1 })));
        Assert.Contains(((SqlException)lane.InnerException!).Number, new[] { 2601, 2627 });
        var taken = await Assert.ThrowsAsync<DbUpdateException>(() => host.UseDbAsync(db => db.RaftPhotos.Add(new RaftPhoto { RaftId = raftId, SortOrder = 1, FileKey = "raft-photos/x.png" })));
        Assert.Contains(((SqlException)taken.InnerException!).Number, new[] { 2601, 2627 });
        foreach (var slot in new[] { 0, Raft.MaxPhotos + 1 })
        {
            var outside = await Assert.ThrowsAsync<DbUpdateException>(() => host.UseDbAsync(db => db.RaftPhotos.Add(new RaftPhoto { RaftId = raftId, SortOrder = slot, FileKey = "raft-photos/x.png" })));
            Assert.Contains("CK_RaftPhoto_SortOrder", outside.InnerException!.Message);
        }

        // New lanes get the status In Use; the catalog holds the LANE group.
        Assert.Equal(StatusIds.Lane.InUse, await host.QueryAsync(db => db.Lanes.Where(l => l.RaftId == raftId).Select(l => l.StatusId).SingleAsync()));
        Assert.Equal(new[] { "IN_USE", "REMOVED" }, await host.QueryAsync(db => db.Statuses.Where(s => s.Group == StatusGroups.Lane).OrderBy(s => s.Id).Select(s => s.Code).ToListAsync()));
    }

    // The zone map, the proposal and the views read lanes and photos as SQL, not in memory.
    [SqlServerFact]
    public async Task Map_proposal_list_and_lanes_of_today_run_on_sql_server()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zone = await owner.CreateZoneAsync();
        var body = NewRaftBody(lanes: 3);
        var pinned = await owner.CreateRaftAsync(zone.Id(), body);
        await owner.CreateRaftAsync(zone.Id(), NewRaftBody() with { Latitude = null, Longitude = null });
        await owner.EditRaftAsync(pinned.Id(), body with { LaneCount = 1, RowVersion = pinned.Text("rowVersion") });
        var staff = await host.StaffInZoneAsync(zone.Id());

        var map = (await staff.Client.GetFromJsonAsync<JsonElement>("/api/zones/map")).EnumerateArray().Single();
        Assert.Equal([pinned.Id()], map.GetProperty("rafts").EnumerateArray().Select(r => r.Id()));
        Assert.Equal($"{zone.Text("code")}-B01", (await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zone.Id()}/rafts/next-code")).Text("code"));
        var rows = (await staff.Client.GetFromJsonAsync<JsonElement>($"/api/zones/{zone.Id()}/rafts")).EnumerateArray().ToList();
        Assert.Contains(rows, r => r.Id() == pinned.Id() && r.GetProperty("laneCount").GetInt32() == 1);
        var today = await staff.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{pinned.Id()}/lanes/today");
        Assert.Equal(["In Use", "Removed", "Removed"], today.GetProperty("lanes").EnumerateArray().Select(l => l.Text("status")));
    }
}
