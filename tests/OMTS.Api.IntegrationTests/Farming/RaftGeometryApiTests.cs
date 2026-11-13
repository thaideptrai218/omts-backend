using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// Decision of 12/11/2026, the geometry of a raft: length and width in metres, the area worked out, a line length per lane, the legacy size text kept in step in both directions, and
/// BR-FARM-04 kept (lanes are added or removed at the end only, and only while no cycle is open).
/// </summary>
public class RaftGeometryApiTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static RaftBody Geometry(string code, int lanes = 6, decimal length = 24m, decimal width = 10m) =>
        new(code, 21.05m, 107.35m, "Raft row 2", null, null, lanes, LengthM: length, WidthM: width);

    private async Task<(Actor Owner, Guid ZoneId)> OwnerWithZoneAsync()
    {
        var owner = await factory.OwnerAsync();
        return (owner, (await owner.Client.CreateZoneAsync()).Id());
    }

    [Fact]
    public async Task A_raft_with_length_and_width_returns_the_area_the_total_line_and_the_legacy_size_text()
    {
        var (owner, zoneId) = await OwnerWithZoneAsync();

        var raft = await owner.Client.CreateRaftAsync(zoneId, Geometry(NewCode()));

        Assert.Equal((24m, 10m, 240m, 2m * 24m * 6, "24 m x 10 m"), (raft.GetProperty("lengthM").GetDecimal(), raft.GetProperty("widthM").GetDecimal(),
            raft.GetProperty("areaM2").GetDecimal(), raft.GetProperty("totalLineLengthM").GetDecimal(), raft.Text("size")));
        var lane = raft.GetProperty("lanes")[2];
        Assert.Equal((24m, false, false, false), (lane.GetProperty("lineLengthM").GetDecimal(), lane.Flag("lineLengthOverridden"), lane.Flag("isDamaged"), lane.Flag("isOutOfService")));
        var listed = (await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zones/{zoneId}/rafts")).EnumerateArray().Single();
        Assert.Equal((24m, 10m, 240m), (listed.GetProperty("lengthM").GetDecimal(), listed.GetProperty("widthM").GetDecimal(), listed.GetProperty("areaM2").GetDecimal()));
    }

    [Fact]
    public async Task The_area_is_never_taken_from_the_client_and_the_size_text_follows_the_numbers_even_when_a_size_is_sent()
    {
        var (owner, zoneId) = await OwnerWithZoneAsync();
        var body = Geometry(NewCode(), length: 18.5m, width: 9.25m) with { Size = "999 m x 1 m" };

        var raft = await owner.Client.CreateRaftAsync(zoneId, body);

        Assert.Equal(("18.5 m x 9.25 m", 171.13m), (raft.Text("size"), raft.GetProperty("areaM2").GetDecimal())); // 171.125 half up
    }

    [Fact]
    public async Task An_older_client_that_sends_only_the_size_text_gets_the_numbers_read_from_it_or_none_never_guessed()
    {
        var (owner, zoneId) = await OwnerWithZoneAsync();

        var read = await owner.Client.CreateRaftAsync(zoneId, NewRaftBody() with { Size = "10 m x 24 m" });
        var free = await owner.Client.CreateRaftAsync(zoneId, NewRaftBody() with { Size = "about the size of a boat" });

        Assert.Equal((10m, 24m, 240m), (read.GetProperty("lengthM").GetDecimal(), read.GetProperty("widthM").GetDecimal(), read.GetProperty("areaM2").GetDecimal()));
        Assert.Equal(JsonValueKind.Null, free.GetProperty("lengthM").ValueKind);
        Assert.Equal(JsonValueKind.Null, free.GetProperty("areaM2").ValueKind);
        Assert.Equal("about the size of a boat", free.Text("size"));
    }

    [Theory]
    [InlineData(0.99, 10, "Length")]
    [InlineData(500.01, 10, "Length")]
    [InlineData(24.005, 10, "Length")]
    [InlineData(24, 200.01, "Width")]
    [InlineData(24, 0, "Width")]
    public async Task A_length_or_width_out_of_range_or_with_a_third_decimal_is_refused_by_name(double length, double width, string field)
    {
        var (owner, zoneId) = await OwnerWithZoneAsync();

        var response = await owner.Client.PostAsJsonAsync($"/api/zones/{zoneId}/rafts", Geometry(NewCode(), length: (decimal)length, width: (decimal)width));

        Assert.Contains(field, await response.InvalidFieldsAsync());
    }

    [Fact]
    public async Task One_side_alone_is_refused_and_both_are_named()
    {
        var (owner, zoneId) = await OwnerWithZoneAsync();

        var response = await owner.Client.PostAsJsonAsync($"/api/zones/{zoneId}/rafts", Geometry(NewCode()) with { WidthM = null });

        Assert.Equal(["Width"], await response.InvalidFieldsAsync());
    }

    [Fact]
    public async Task A_lane_can_have_a_line_of_its_own_up_to_the_length_of_the_raft_and_goes_back_with_null()
    {
        var (owner, zoneId) = await OwnerWithZoneAsync();
        var body = Geometry(NewCode(), lanes: 4) with { LaneLengths = [new LaneLengthBody(3, 18.5m)] };

        var raft = await owner.Client.CreateRaftAsync(zoneId, body);

        var lane3 = raft.GetProperty("lanes")[2];
        Assert.Equal((18.5m, true), (lane3.GetProperty("lineLengthM").GetDecimal(), lane3.Flag("lineLengthOverridden")));
        Assert.Equal(2m * (24m * 3 + 18.5m), raft.GetProperty("totalLineLengthM").GetDecimal());

        var back = await owner.Client.EditRaftAsync(raft.Id(), body with { RowVersion = raft.Text("rowVersion"), LaneLengths = [new LaneLengthBody(3, null)] });
        Assert.Equal((24m, false), (back.GetProperty("lanes")[2].GetProperty("lineLengthM").GetDecimal(), back.GetProperty("lanes")[2].Flag("lineLengthOverridden")));
        var audit = await factory.QueryAsync(db => Task.FromResult(db.AuditEvents.Count(a => a.Action == "Raft.LaneLengthChanged" && a.RecordId == raft.Id())));
        Assert.Equal(1, audit);
    }

    [Theory]
    [InlineData(24.01)]
    [InlineData(0.5)]
    public async Task A_lane_line_longer_than_the_raft_or_shorter_than_a_metre_is_refused(double line)
    {
        var (owner, zoneId) = await OwnerWithZoneAsync();

        var response = await owner.Client.PostAsJsonAsync($"/api/zones/{zoneId}/rafts", Geometry(NewCode()) with { LaneLengths = [new LaneLengthBody(1, (decimal)line)] });

        Assert.Equal(["Line length"], await response.InvalidFieldsAsync());
    }

    [Fact]
    public async Task A_line_for_a_lane_the_raft_does_not_have_is_refused()
    {
        var (owner, zoneId) = await OwnerWithZoneAsync();

        var response = await owner.Client.PostAsJsonAsync($"/api/zones/{zoneId}/rafts", Geometry(NewCode(), lanes: 4) with { LaneLengths = [new LaneLengthBody(9, 10m)] });

        var problem = await response.ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Line length", problem.Text("field"));
    }

    [Fact]
    public async Task A_shorter_raft_is_refused_while_a_lane_keeps_a_longer_line_and_names_the_lanes()
    {
        var (owner, zoneId) = await OwnerWithZoneAsync();
        var raft = await owner.Client.CreateRaftAsync(zoneId, Geometry(NewCode(), lanes: 4) with { LaneLengths = [new LaneLengthBody(2, 20m)] });

        var response = await owner.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", Geometry(raft.Text("code"), lanes: 4, length: 15m) with { RowVersion = raft.Text("rowVersion") });

        var problem = await response.ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Length", problem.Text("field"));
        Assert.Equal([2], problem.GetProperty("lanes").EnumerateArray().Select(l => l.GetInt32()).ToList());
        var unchanged = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}");
        Assert.Equal(24m, unchanged.GetProperty("lengthM").GetDecimal());
    }

    [Fact]
    public async Task Lanes_narrower_than_a_metre_and_lanes_of_unequal_length_warn_without_blocking()
    {
        var (owner, zoneId) = await OwnerWithZoneAsync();

        var saved = await (await owner.Client.PostAsJsonAsync(
            $"/api/zones/{zoneId}/rafts", Geometry(NewCode(), lanes: 12, width: 5m) with { LaneLengths = [new LaneLengthBody(1, 10m)] })).ReadAsync(HttpStatusCode.Created);

        var codes = saved.GetProperty("warnings").EnumerateArray().Select(w => w.Text("code")).ToList();
        Assert.Contains("RAFT_NARROW_FOR_LANES", codes);
        Assert.Contains("RAFT_LANE_LENGTHS_DIFFER", codes);
    }

    [Fact]
    public async Task Changing_the_geometry_is_audited_and_keeps_the_size_text_in_step()
    {
        var (owner, zoneId) = await OwnerWithZoneAsync();
        var raft = await owner.Client.CreateRaftAsync(zoneId, Geometry(NewCode()));

        var edited = await owner.Client.EditRaftAsync(raft.Id(), Geometry(raft.Text("code"), length: 30m, width: 12m) with { RowVersion = raft.Text("rowVersion") });

        Assert.Equal(("30 m x 12 m", 360m), (edited.Text("size"), edited.GetProperty("areaM2").GetDecimal()));
        var updated = await factory.QueryAsync(db => Task.FromResult(db.AuditEvents.Single(a => a.Action == "Raft.Updated" && a.RecordId == raft.Id())));
        Assert.Contains("LengthM", updated.After);
        Assert.Contains("WidthM", updated.After);
    }

    [Fact]
    public async Task The_number_of_lanes_still_cannot_change_while_a_cycle_is_open_but_the_geometry_can()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var raft = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{setup.RaftId}");

        var moreLanes = await owner.PutAsJsonAsync(
            $"/api/rafts/{setup.RaftId}", Geometry(raft.Text("code"), lanes: 7) with { RowVersion = raft.Text("rowVersion") });
        var sameLanes = await owner.PutAsJsonAsync(
            $"/api/rafts/{setup.RaftId}", Geometry(raft.Text("code"), lanes: 5, length: 30m, width: 12m) with { RowVersion = raft.Text("rowVersion") });

        Assert.Equal("MSG-GEN-06", (await moreLanes.ReadAsync(HttpStatusCode.Conflict)).Text("code"));
        Assert.Equal(360m, (await sameLanes.ReadAsync(HttpStatusCode.OK)).GetProperty("raft").GetProperty("areaM2").GetDecimal());
    }
}
