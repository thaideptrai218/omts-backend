using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// The licensed sea area of a zone, read by the app from the photographed map (<c>PUT api/zones/{id}/boundary</c>), and the
/// raft warnings it brings. PM decision 25/11/2026 (warn, never block): a raft pinned outside it saves with a warning.
/// </summary>
public class ZoneBoundaryTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Changed = "FarmingZone.BoundaryChanged";

    // About 2.1 km east to west by 2.2 km north to south around the zone pin of NewZoneBody (21.05, 107.35), as an open ring.
    private static readonly double[][] Square = [[107.34, 21.06], [107.36, 21.06], [107.36, 21.04], [107.34, 21.04]];

    // The same area closed, in the GeoJSON the server stores and answers.
    private const string SquareJson =
        """{"type":"FeatureCollection","features":[{"type":"Feature","properties":{"name":"Khu 1","corners":["A1","A2","A3","A4"]},"geometry":"""
        + """{"type":"Polygon","coordinates":[[[107.34,21.06],[107.36,21.06],[107.36,21.04],[107.34,21.04],[107.34,21.06]]]}}]}""";

    [Fact]
    public async Task A_boundary_is_saved_normalized_shown_by_the_zone_and_the_map_audited_and_cleared_with_null()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await owner.CreateZoneAsync();
        Assert.Equal(JsonValueKind.Null, zone.GetProperty("boundary").ValueKind);

        // An open ring with an altitude and more than 6 decimals; the name and the labels with spaces around.
        var sent = Collection(Area([[107.3400004, 21.06], [107.36, 21.06, 2.5], [107.36, 21.04], [107.34, 21.04]], " Khu 1 ", [" A1", "A2", "A3", "A4 "]));
        var saved = await (await PutBoundaryAsync(owner, zone.Id(), sent, zone.Text("rowVersion"))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("MSG-GEN-05", "Farming zone saved."), (saved.Text("messageCode"), saved.Text("message")));
        Assert.Empty(saved.GetProperty("warnings").EnumerateArray());
        Assert.Equal(SquareJson, saved.GetProperty("zone").GetProperty("boundary").GetRawText());

        Assert.Equal(SquareJson, (await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zone.Id()}")).GetProperty("boundary").GetRawText());
        var onMap = (await owner.GetFromJsonAsync<JsonElement>("/api/zones/map")).EnumerateArray().Single(z => z.Id() == zone.Id());
        Assert.Equal(SquareJson, onMap.GetProperty("boundary").GetRawText());
        Assert.Equal(SquareJson, (await factory.ZoneEntityAsync(zone.Id())).BoundaryGeoJson);

        var audit = Assert.Single(await AuditsAsync(zone.Id()));
        Assert.Null(JsonNode.Parse(audit.Before!)!["Boundary"]);
        Assert.Equal("FeatureCollection", JsonNode.Parse(audit.After!)!["Boundary"]!["type"]!.GetValue<string>());

        // The same area sent closed changes nothing, so nothing is logged.
        var again = Collection(Area([.. Square, Square[0]], "Khu 1", ["A1", "A2", "A3", "A4"]));
        var unchanged = await (await PutBoundaryAsync(owner, zone.Id(), again, saved.GetProperty("zone").Text("rowVersion"))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(SquareJson, unchanged.GetProperty("zone").GetProperty("boundary").GetRawText());
        Assert.Single(await AuditsAsync(zone.Id()));

        // A version the server never issued stands for a stale one (InMemory has no rowversion): nothing changes.
        Assert.Equal("MSG06", await (await PutBoundaryAsync(owner, zone.Id(), null, StaleRowVersion)).ErrorCodeAsync());
        Assert.Equal(SquareJson, (await factory.ZoneEntityAsync(zone.Id())).BoundaryGeoJson);

        var cleared = await (await PutBoundaryAsync(owner, zone.Id(), null, unchanged.GetProperty("zone").Text("rowVersion"))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("zone").GetProperty("boundary").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zone.Id()}")).GetProperty("boundary").ValueKind);
        Assert.Null((await factory.ZoneEntityAsync(zone.Id())).BoundaryGeoJson);
        var audits = await AuditsAsync(zone.Id());
        Assert.Equal(2, audits.Count);
        Assert.Null(JsonNode.Parse(audits[1].After!)!["Boundary"]);
    }

    [Fact]
    public async Task Saving_a_boundary_moves_the_zone_pin_to_its_centroid_and_clearing_it_keeps_the_pin()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var code = NewCode();
        var zone = await owner.CreateZoneAsync(new ZoneBody(code, $"Zone {code}", null, null, null, null));
        Assert.Equal(JsonValueKind.Null, zone.GetProperty("latitude").ValueKind);

        // Two equal areas side by side: the centroid is the middle between them.
        double[][] east = [[107.38, 21.06], [107.40, 21.06], [107.40, 21.04], [107.38, 21.04]];
        var saved = (await (await PutBoundaryAsync(owner, zone.Id(), Collection(Area(Square), Area(east, "Khu 2")), zone.Text("rowVersion"))).ReadAsync(HttpStatusCode.OK)).GetProperty("zone");
        Assert.Equal((21.05m, 107.37m), (saved.GetProperty("latitude").GetDecimal(), saved.GetProperty("longitude").GetDecimal()));

        var cleared = (await (await PutBoundaryAsync(owner, zone.Id(), null, saved.Text("rowVersion"))).ReadAsync(HttpStatusCode.OK)).GetProperty("zone");
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("boundary").ValueKind);
        Assert.Equal((21.05m, 107.37m), (cleared.GetProperty("latitude").GetDecimal(), cleared.GetProperty("longitude").GetDecimal()));
    }

    [Fact]
    public async Task An_edit_without_a_pin_keeps_the_pin_of_a_zone_with_a_boundary_and_wipes_it_without_one()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var withBoundary = await owner.CreateZoneAsync(new ZoneBody(NewCode(), "Có ranh giới", null, null, null, null));
        var boundaryZone = (await (await PutBoundaryAsync(owner, withBoundary.Id(), Collection(Area(Square)), withBoundary.Text("rowVersion"))).ReadAsync(HttpStatusCode.OK)).GetProperty("zone");
        var plain = await owner.CreateZoneAsync();

        var kept = await owner.EditZoneAsync(
            boundaryZone.Id(), new ZoneBody(boundaryZone.Text("code"), "Tên mới", "Vân Đồn", null, null, 3m, boundaryZone.Text("rowVersion")));
        var wiped = await owner.EditZoneAsync(plain.Id(), new ZoneBody(plain.Text("code"), plain.Text("name"), null, null, null, null, plain.Text("rowVersion")));

        Assert.Equal((21.05m, 107.35m), (kept.GetProperty("latitude").GetDecimal(), kept.GetProperty("longitude").GetDecimal()));
        Assert.Equal("Tên mới", kept.Text("name"));
        Assert.Equal(JsonValueKind.Null, wiped.GetProperty("latitude").ValueKind);
    }

    [Fact]
    public async Task An_invalid_boundary_is_refused_naming_the_part_and_nothing_is_saved()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await owner.CreateZoneAsync();
        async Task<List<string>> RefusedAsync(object boundary) => await (await PutBoundaryAsync(owner, zone.Id(), boundary, zone.Text("rowVersion"))).InvalidFieldsAsync();

        Assert.Equal(["Boundary"], await RefusedAsync(new { type = "Feature", features = new[] { Area(Square) } }));
        Assert.Equal(["Boundary"], await RefusedAsync(Collection()));
        Assert.Equal(["Boundary"], await RefusedAsync(Collection([.. Enumerable.Repeat(Area(Square), 11)])));
        Assert.Equal(["Boundary"], await RefusedAsync(Collection(new { type = "Feature", properties = (object?)null, geometry = new { type = "Polygon", coordinates = new[] { Square, Square } } })));
        Assert.Equal(["Boundary"], await RefusedAsync(Collection(new { type = "Feature", properties = (object?)null, geometry = new { type = "MultiPolygon", coordinates = new[] { Square } } })));
        Assert.Equal(["Boundary"], await RefusedAsync(Collection(Area([[107.34, 21.06], [107.36, 21.06], [107.34, 21.06]]))));
        Assert.Equal(["Boundary coordinates"], await RefusedAsync(Collection(Area([[21.06, 107.34], [21.06, 107.36], [21.04, 107.36]]))));
        Assert.Equal(["Boundary coordinates"], await RefusedAsync(Collection(Area([[0, 0], [107.36, 21.06], [107.36, 21.04]]))));
        Assert.Equal(["Boundary name"], await RefusedAsync(Collection(Area(Square, new string('k', 101)))));
        Assert.Equal(["Boundary corners"], await RefusedAsync(Collection(Area(Square, "Khu 1", ["A1", "A2", "A3"]))));
        Assert.Equal(["Boundary", "Boundary coordinates"], await RefusedAsync(Collection(Area([[107.34, 21.06], [107.36, 21.06]]), Area([[0, 0], [107.36, 21.06], [107.36, 21.04]]))));

        // A value that is not a number cannot be read: MSG-GEN-01 like every unreadable field.
        var unreadable = await owner.PutAsJsonAsync($"/api/zones/{zone.Id()}/boundary", new
        {
            boundary = Collection(new { type = "Feature", geometry = new { type = "Polygon", coordinates = new[] { new[] { new[] { "east", "north" } } } } }),
            rowVersion = zone.Text("rowVersion"),
        });
        Assert.Equal("MSG-GEN-01", await unreadable.ErrorCodeAsync());

        Assert.Null((await factory.ZoneEntityAsync(zone.Id())).BoundaryGeoJson);
        Assert.Empty(await AuditsAsync(zone.Id()));
    }

    [Fact]
    public async Task A_raft_pinned_outside_the_boundary_saves_with_its_distance_and_one_inside_or_on_an_edge_without()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await owner.CreateZoneAsync();
        double[][] second = [[107.38, 21.06], [107.40, 21.06], [107.40, 21.04], [107.38, 21.04]];
        await (await PutBoundaryAsync(owner, zone.Id(), Collection(Area(Square), Area(second, "Khu 2")), zone.Text("rowVersion"))).ReadAsync(HttpStatusCode.OK);

        Assert.Empty(Warnings(await CreateRaftAsync(owner, zone.Id(), NewRaftBody())));
        Assert.Empty(Warnings(await CreateRaftAsync(owner, zone.Id(), NewRaftBody() with { Latitude = 21.06m }))); // on the north edge
        Assert.Empty(Warnings(await CreateRaftAsync(owner, zone.Id(), NewRaftBody() with { Longitude = 107.39m }))); // inside Khu 2
        Assert.Empty(Warnings(await CreateRaftAsync(owner, zone.Id(), NewRaftBody() with { Latitude = null, Longitude = null })));

        // 0.01 degree east of the east edge of Khu 1, about 1.04 km: within 5 km of the zone pin, so only this warning.
        var outside = await CreateRaftAsync(owner, zone.Id(), NewRaftBody() with { Longitude = 107.37m });
        var warning = Assert.Single(outside.GetProperty("warnings").EnumerateArray());
        Assert.Equal("RAFT_OUTSIDE_ZONE_BOUNDARY", warning.Text("code"));
        Assert.Equal(1038, warning.GetProperty("details").GetProperty("distanceM").GetInt32());
        Assert.Contains("1038 m outside the zone boundary", warning.Text("message"));
        var raft = outside.GetProperty("raft");
        Assert.Equal(107.37m, raft.GetProperty("longitude").GetDecimal());

        // Moved inside by an edit, the warning goes; moved out again, it comes back.
        var moved = await EditRaftAsync(owner, raft.Id(), NewRaftBody(raft.Text("code")) with { RowVersion = raft.Text("rowVersion") });
        Assert.Empty(Warnings(moved));
        var back = await EditRaftAsync(owner, raft.Id(), NewRaftBody(raft.Text("code")) with { Longitude = 107.37m, RowVersion = moved.GetProperty("raft").Text("rowVersion") });
        Assert.Equal(["RAFT_OUTSIDE_ZONE_BOUNDARY"], Warnings(back));

        // Far from the zone pin too: both warnings, the 5 km one unchanged.
        Assert.Equal(["RAFT_FAR_FROM_ZONE", "RAFT_OUTSIDE_ZONE_BOUNDARY"], Warnings(await CreateRaftAsync(owner, zone.Id(), NewRaftBody() with { Latitude = 21.15m })));
    }

    [Fact]
    public async Task Saving_a_boundary_names_the_in_use_rafts_pinned_outside_it_and_still_saves()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await owner.CreateZoneAsync();
        await owner.CreateRaftAsync(zone.Id(), NewRaftBody("B-IN"));
        await owner.CreateRaftAsync(zone.Id(), NewRaftBody("B-OUT2") with { Longitude = 107.37m });
        await owner.CreateRaftAsync(zone.Id(), NewRaftBody("B-OUT1") with { Latitude = 21.03m });
        await owner.CreateRaftAsync(zone.Id(), NewRaftBody("B-NOPIN") with { Latitude = null, Longitude = null });
        var stopped = await owner.CreateRaftAsync(zone.Id(), NewRaftBody("B-STOP") with { Longitude = 107.37m });
        await (await owner.PostAsJsonAsync($"/api/rafts/{stopped.Id()}/discontinue", new { rowVersion = stopped.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);

        var saved = await (await PutBoundaryAsync(owner, zone.Id(), Collection(Area(Square)), zone.Text("rowVersion"))).ReadAsync(HttpStatusCode.OK);

        var warning = Assert.Single(saved.GetProperty("warnings").EnumerateArray());
        Assert.Equal("RAFTS_OUTSIDE_ZONE_BOUNDARY", warning.Text("code"));
        var details = warning.GetProperty("details");
        Assert.Equal(2, details.GetProperty("count").GetInt32());
        Assert.Equal(["B-OUT1", "B-OUT2"], details.GetProperty("raftCodes").EnumerateArray().Select(c => c.GetString()));
        Assert.Contains("B-OUT1, B-OUT2", warning.Text("message"));
        Assert.NotNull((await factory.ZoneEntityAsync(zone.Id())).BoundaryGeoJson);

        // Without a boundary nothing is outside it.
        var cleared = await (await PutBoundaryAsync(owner, zone.Id(), null, saved.GetProperty("zone").Text("rowVersion"))).ReadAsync(HttpStatusCode.OK);
        Assert.Empty(cleared.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task Only_the_owner_sets_the_boundary_and_only_of_an_in_use_zone()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await owner.CreateZoneAsync();
        var entity = await factory.ZoneEntityAsync(zone.Id());
        var url = $"/api/zones/{zone.Id()}/boundary";
        var body = new { boundary = Collection(Area(Square)), rowVersion = zone.Text("rowVersion") };

        // Writes are the Owner's, as the zone edit: staff of the zone, whatever their role, and traders are refused.
        foreach (var role in new[] { StaffRole.ZoneManager, StaffRole.Accountant })
        {
            var staff = await factory.StaffAsync(entity, role);
            Assert.Equal("MSG-GEN-08", await (await staff.Client.PutAsJsonAsync(url, body)).ErrorCodeAsync());
        }

        Assert.Equal("MSG-GEN-08", await (await (await factory.TraderAsync()).PutAsJsonAsync(url, body)).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PutAsJsonAsync(url, body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync($"/api/zones/{Guid.NewGuid()}/boundary", body)).StatusCode);
        Assert.Null((await factory.ZoneEntityAsync(zone.Id())).BoundaryGeoJson);

        // A Discontinued zone is read-only until it is resumed (GBR-01, MSG-GEN-09).
        var discontinued = await (await owner.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = zone.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("MSG-GEN-09", await (await PutBoundaryAsync(owner, zone.Id(), Collection(Area(Square)), discontinued.Text("rowVersion"))).ErrorCodeAsync());
        Assert.Null((await factory.ZoneEntityAsync(zone.Id())).BoundaryGeoJson);
    }

    private static Task<HttpResponseMessage> PutBoundaryAsync(HttpClient client, Guid zoneId, object? boundary, string rowVersion) =>
        client.PutAsJsonAsync($"/api/zones/{zoneId}/boundary", new { boundary, rowVersion });

    private static object Collection(params object[] features) => new { type = "FeatureCollection", features };

    private static object Area(double[][] corners, string? name = null, string[]? labels = null) => new
    {
        type = "Feature",
        properties = name is null && labels is null ? null : new { name, corners = labels },
        geometry = new { type = "Polygon", coordinates = new[] { corners } },
    };

    private static async Task<JsonElement> CreateRaftAsync(HttpClient owner, Guid zoneId, RaftBody body) =>
        await (await owner.PostAsJsonAsync($"/api/zones/{zoneId}/rafts", body)).ReadAsync(HttpStatusCode.Created);

    private static async Task<JsonElement> EditRaftAsync(HttpClient owner, Guid raftId, RaftBody body) =>
        await (await owner.PutAsJsonAsync($"/api/rafts/{raftId}", body)).ReadAsync(HttpStatusCode.OK);

    private static List<string> Warnings(JsonElement answer) => answer.GetProperty("warnings").EnumerateArray().Select(w => w.Text("code")).ToList();

    private Task<List<AuditEvent>> AuditsAsync(Guid zoneId) =>
        factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == zoneId && e.Action == Changed).OrderBy(e => e.OccurredAt).ToListAsync());
}
