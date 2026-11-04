using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>Independent QA of FARM-04 rules: lane count, open cycle, next code, map pin, Discontinued raft and audit.</summary>
public class RaftsV2QaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly int InUse = StatusIds.Lane.InUse;
    private static readonly int Removed = StatusIds.Lane.Removed;

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(201)]
    [InlineData(int.MaxValue)]
    [InlineData(null)]
    public async Task A_lane_count_out_of_range_on_edit_changes_nothing(int? count)
    {
        var owner = (await factory.OwnerAsync()).Client;
        var body = NewRaftBody(lanes: 3);
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id(), body);

        var answer = await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with { LaneCount = count, ConditionNotes = "Changed", RowVersion = raft.Text("rowVersion") });

        Assert.Equal(["Number of lanes"], await answer.InvalidFieldsAsync());
        var kept = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}");
        Assert.Equal([1, 2, 3], kept.LaneNumbers(InUse));
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("conditionNotes").ValueKind);
        Assert.Equal(0, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == raft.Id() && e.Action != "Raft.Created")));
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("2.5")]
    [InlineData("99999999999")]
    [InlineData("true")]
    public async Task An_unreadable_lane_count_answers_msg_gen_01(string json)
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await owner.CreateZoneAsync();
        var content = new StringContent(
            $$"""{"code":"{{NewCodeForJson()}}","size":"10 m","laneCount":{{json}}}""", System.Text.Encoding.UTF8, "application/json");

        var answer = await owner.PostAsync($"/api/zones/{zone.Id()}/rafts", content);

        // Named "request", not the lane field: the known behaviour of every unreadable JSON value of the API (see WaterBusinessTimeQaTests).
        Assert.Equal(["request"], await answer.InvalidFieldsAsync());
        Assert.Empty(await factory.QueryAsync(db => db.Rafts.Where(r => r.ZoneId == zone.Id()).ToListAsync()));
    }

    // The 200 limit is on lanes In Use: going down and up again creates no new numbers, so the raft never holds more than 200 rows.
    [Fact]
    public async Task The_limit_of_200_is_accepted_and_a_way_down_and_up_reuses_the_same_rows()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var body = NewRaftBody(lanes: 200);
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id(), body);
        Assert.Equal(200, raft.GetProperty("laneCount").GetInt32());
        var ids = Enumerable.Range(1, 200).Select(n => raft.LaneId(n)).ToList();

        var one = await owner.EditRaftAsync(raft.Id(), body with { LaneCount = 1, RowVersion = raft.Text("rowVersion") });
        Assert.Equal(Enumerable.Range(2, 199), one.LaneNumbers(Removed));

        var all = await owner.EditRaftAsync(raft.Id(), body with { LaneCount = 200, RowVersion = one.Text("rowVersion") });
        Assert.Equal(ids, Enumerable.Range(1, 200).Select(n => all.LaneId(n)));
        Assert.Equal(200, await factory.QueryAsync(db => db.Lanes.CountAsync(l => l.RaftId == raft.Id())));

        var more = await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with { LaneCount = 201, RowVersion = all.Text("rowVersion") });
        Assert.Equal(["Number of lanes"], await more.InvalidFieldsAsync());
    }

    // Remove takes the last lanes In Use, add restores the lowest Removed first; a partial restore leaves the highest Removed.
    [Fact]
    public async Task Numbers_stay_stable_over_several_removes_and_adds()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var body = NewRaftBody(lanes: 6);
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id(), body);
        var version = raft.Text("rowVersion");

        async Task<JsonElement> ToAsync(int count)
        {
            var saved = await owner.EditRaftAsync(raft.Id(), body with { LaneCount = count, RowVersion = version });
            version = saved.Text("rowVersion");
            return saved;
        }

        var four = await ToAsync(4);
        Assert.Equal([5, 6], four.LaneNumbers(Removed));
        var two = await ToAsync(2);
        Assert.Equal([3, 4, 5, 6], two.LaneNumbers(Removed));
        var three = await ToAsync(3);
        Assert.Equal([1, 2, 3], three.LaneNumbers(InUse));
        Assert.Equal([4, 5, 6], three.LaneNumbers(Removed));
        var nine = await ToAsync(9);
        Assert.Equal(Enumerable.Range(1, 9), nine.LaneNumbers(InUse));
        var one = await ToAsync(1);
        Assert.Equal([1], one.LaneNumbers(InUse));
        Assert.Equal(Enumerable.Range(1, 9), one.GetProperty("lanes").EnumerateArray().Select(l => l.GetProperty("laneNumber").GetInt32()));
        Assert.Equal(1, (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}")).GetProperty("laneCount").GetInt32());
    }

    // BR-FARM-04: the same number of lanes with an open cycle saves; only a real change is blocked, and it saves nothing and audits nothing.
    [Fact]
    public async Task An_open_cycle_blocks_only_a_real_lane_change_and_leaves_no_trace()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var body = NewRaftBody(lanes: 4);
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id(), body);
        var two = await owner.EditRaftAsync(raft.Id(), body with { LaneCount = 2, RowVersion = raft.Text("rowVersion") });
        var cycle = await factory.AddCycleAsync(raft.Id(), DateTimeOffset.UtcNow.AddDays(-1));
        var before = await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == raft.Id()));

        // A restore is a lane change too: 2 -> 3 brings a Removed lane back, and is blocked.
        var restore = await (await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with { Code = "CHANGED-CODE", LaneCount = 3, RowVersion = two.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-06", cycle.Id), (restore.Text("code"), restore.GetProperty("cycleId").GetGuid()));
        Assert.Contains("cannot proceed", restore.Text("detail"), StringComparison.OrdinalIgnoreCase);

        var kept = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}");
        Assert.Equal(raft.Text("code"), kept.Text("code"));
        Assert.Equal([1, 2], kept.LaneNumbers(InUse));
        Assert.Equal(before, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == raft.Id())));

        // An invalid number answers the field, not the cycle.
        var invalid = await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with { LaneCount = 0, RowVersion = two.Text("rowVersion") });
        Assert.Equal(["Number of lanes"], await invalid.InvalidFieldsAsync());

        var same = await owner.EditRaftAsync(raft.Id(), body with { Code = "CHANGED-CODE", LaneCount = 2, RowVersion = two.Text("rowVersion") });
        Assert.Equal("CHANGED-CODE", same.Text("code"));

        // Only the 4 -> 2 before the cycle is a lane change.
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == raft.Id() && e.Action == "Raft.LanesChanged")));
    }

    // GBR-03: format, gaps, case, more than two digits, codes of the zone only, a renamed zone, a zone code too long for the pattern.
    [Fact]
    public async Task Next_code_takes_the_highest_number_ignoring_case_and_gaps()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await owner.CreateZoneAsync();
        var code = zone.Text("code");
        var url = $"/api/zones/{zone.Id()}/rafts/next-code";
        async Task<string> NextAsync() => (await owner.GetFromJsonAsync<JsonElement>(url)).Text("code");

        await owner.CreateRaftAsync(zone.Id(), NewRaftBody($"{code}-B02"));
        Assert.Equal($"{code}-B03", await NextAsync());
        await owner.CreateRaftAsync(zone.Id(), NewRaftBody($"{code.ToLowerInvariant()}-b09"));
        Assert.Equal($"{code}-B10", await NextAsync());
        await owner.CreateRaftAsync(zone.Id(), NewRaftBody($"{code}-B099"));
        Assert.Equal($"{code}-B100", await NextAsync());
        await owner.CreateRaftAsync(zone.Id(), NewRaftBody($"{code}-B100X"));
        Assert.Equal($"{code}-B100", await NextAsync());

        // The proposal can be saved as it is, and is then taken.
        await owner.CreateRaftAsync(zone.Id(), NewRaftBody(await NextAsync()));
        Assert.Equal($"{code}-B101", await NextAsync());
    }

    [Fact]
    public async Task Next_code_of_a_renamed_or_long_named_zone_is_free_and_fits_the_field()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var body = NewZoneBody();
        var zone = await owner.CreateZoneAsync(body);
        await owner.CreateRaftAsync(zone.Id(), NewRaftBody($"{body.Code}-B07"));
        var renamed = $"{body.Code}N";
        await owner.EditZoneAsync(zone.Id(), body with { Code = renamed, RowVersion = (await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zone.Id()}")).Text("rowVersion") });
        Assert.Equal($"{renamed}-B01", (await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zone.Id()}/rafts/next-code")).Text("code"));

        var longName = NewZoneBody(new string('Z', 50 - 32) + Guid.NewGuid().ToString("N"));
        var longZone = await owner.CreateZoneAsync(longName);
        var proposal = (await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{longZone.Id()}/rafts/next-code")).Text("code");
        Assert.Equal("B01", proposal);
        await owner.CreateRaftAsync(longZone.Id(), NewRaftBody(proposal));
        Assert.Equal("B02", (await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{longZone.Id()}/rafts/next-code")).Text("code"));
    }

    [Fact]
    public async Task Map_pin_answers_name_the_field_and_clearing_it_is_audited_with_nulls()
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();
        var body = NewRaftBody("PIN-QA");
        var raft = await owner.Client.CreateRaftAsync(zone.Id(), body);
        var url = $"/api/rafts/{raft.Id()}";
        var version = raft.Text("rowVersion");

        Assert.Equal(["Latitude"], await (await owner.Client.PutAsJsonAsync(url, body with { Latitude = null, RowVersion = version })).InvalidFieldsAsync());
        Assert.Equal(["Longitude"], await (await owner.Client.PutAsJsonAsync(url, body with { Longitude = null, RowVersion = version })).InvalidFieldsAsync());
        Assert.Equal(["Latitude"], await (await owner.Client.PutAsJsonAsync(url, body with { Latitude = -90.01m, RowVersion = version })).InvalidFieldsAsync());
        Assert.Equal(["Longitude"], await (await owner.Client.PutAsJsonAsync(url, body with { Longitude = 180.01m, RowVersion = version })).InvalidFieldsAsync());

        // The edges and zero are a pin; a zero pin stays on the map.
        var edge = await owner.Client.EditRaftAsync(raft.Id(), body with { Latitude = 0m, Longitude = -180m, RowVersion = version });
        Assert.Equal(0m, edge.GetProperty("latitude").GetDecimal());
        var map = (await owner.Client.GetFromJsonAsync<JsonElement>("/api/zones/map")).EnumerateArray().Single(z => z.Id() == zone.Id());
        Assert.Equal([raft.Id()], map.GetProperty("rafts").EnumerateArray().Select(r => r.Id()));

        await owner.Client.EditRaftAsync(raft.Id(), body with { Latitude = null, Longitude = null, RowVersion = edge.Text("rowVersion") });
        var audit = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == raft.Id() && e.Action == "Raft.Updated").OrderByDescending(e => e.OccurredAt).FirstAsync());
        var after = JsonNode.Parse(audit.After!)!.AsObject();
        Assert.True(after.ContainsKey("Latitude") && after["Latitude"] is null, audit.After);
        Assert.True(after.ContainsKey("Longitude") && after["Longitude"] is null, audit.After);
        Assert.Equal(-180m, JsonNode.Parse(audit.Before!)!["Longitude"]!.GetValue<decimal>());
        Assert.Equal(owner.Account.Id, audit.ActorAccountId);

        var cleared = (await owner.Client.GetFromJsonAsync<JsonElement>("/api/zones/map")).EnumerateArray().Single(z => z.Id() == zone.Id());
        Assert.Equal(0, cleared.GetProperty("rafts").GetArrayLength());
    }

    // GBR-01: a Discontinued raft stays off the map and refuses every write; Discontinue and Resume are audited with the statuses.
    [Fact]
    public async Task Discontinue_and_both_resume_routes_audit_before_and_after_and_keep_lanes_and_code()
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();
        var body = NewRaftBody(lanes: 3);
        var raft = await owner.Client.CreateRaftAsync(zone.Id(), body);
        var two = await owner.Client.EditRaftAsync(raft.Id(), body with { LaneCount = 2, RowVersion = raft.Text("rowVersion") });

        var off = await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { reason = "Storm", rowVersion = two.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        var map = (await owner.Client.GetFromJsonAsync<JsonElement>("/api/zones/map")).EnumerateArray().Single(z => z.Id() == zone.Id());
        Assert.Equal(0, map.GetProperty("rafts").GetArrayLength());

        // A lane change of a Discontinued raft is refused even with an out-of-range number, with no audit row.
        var refused = await (await owner.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with { LaneCount = 999, RowVersion = off.Text("rowVersion") })).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-09", "MSG61"), (refused.Text("code"), refused.Text("legacyCode")));
        Assert.Equal([1, 2], (await owner.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/lanes/today")).GetProperty("lanes").EnumerateArray()
            .Where(l => l.GetProperty("statusId").GetInt32() == InUse).Select(l => l.GetProperty("laneNumber").GetInt32()));

        // A resume without a version is the field, not a 500.
        Assert.Equal(["RowVersion"], await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/resume", new { reason = "x" })).InvalidFieldsAsync());

        var back = await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/resume", new { reason = "Fixed", rowVersion = off.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((raft.Text("code"), 2), (back.Text("code"), back.GetProperty("laneCount").GetInt32()));
        Assert.Equal([3], back.LaneNumbers(Removed));
        var again = await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = back.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/reactivate", new { rowVersion = again.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);

        var audits = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == raft.Id()).OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(["Raft.Created", "Raft.LanesChanged", "Raft.Discontinued", "Raft.Reactivated", "Raft.Discontinued", "Raft.Reactivated"], audits.Select(a => a.Action));
        var stopped = audits[2];
        Assert.Equal("IN_USE", JsonNode.Parse(stopped.Before!)!["Status"]!.GetValue<string>());
        Assert.Equal("OUT_OF_USE", JsonNode.Parse(stopped.After!)!["Status"]!.GetValue<string>());
        Assert.Equal("Storm", stopped.Reason);
        Assert.All(audits, a => Assert.Equal((owner.Account.Id, zone.Id(), raft.Text("code")), (a.ActorAccountId!.Value, a.ZoneId!.Value, a.RecordCode!)));
    }

    // The route is gone with the one-photo model; a body with the old shape only is refused for its missing number.
    [Fact]
    public async Task The_removed_routes_are_gone_and_the_old_body_is_refused()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await owner.CreateZoneAsync();
        var raft = await owner.CreateRaftAsync(zone.Id());

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/rafts/{raft.Id()}/condition")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/rafts/{raft.Id()}/photo", new MultipartFormDataContent { { FileContent(Png, "image/png"), "file", "a.png" } })).StatusCode);

        var old = await owner.PostAsJsonAsync($"/api/zones/{zone.Id()}/rafts", new
        {
            code = "OLD-BODY", size = "1 m", latitude = 21m, longitude = 107m,
            lanes = new[] { new { laneNumber = 1, length = 10m, markerA = "A", markerB = "B" } },
        });
        Assert.Equal(["Number of lanes"], await old.InvalidFieldsAsync());
    }

    private static string NewCodeForJson() => $"J{Guid.NewGuid():N}"[..12];
}
