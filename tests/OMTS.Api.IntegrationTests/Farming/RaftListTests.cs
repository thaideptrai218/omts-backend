using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.WaterQuality;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>The paged raft list: newest code first, filters, the Open cycle of each raft and who may read it.</summary>
public class RaftListTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task The_list_is_paged_newest_code_first_and_each_raft_shows_its_open_cycle_or_null()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var codes = new[] { "R-01", "R-02", "R-03" };
        var rafts = new List<JsonElement>();
        foreach (var code in codes)
        {
            rafts.Add(await owner.CreateRaftAsync(zoneId, NewRaftBody(code)));
        }

        var start = Today.AddDays(-3);
        var cycle = await owner.StartCycleAsync(rafts[1].Id(), new CycleBody(start));

        var first = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts?zoneId={zoneId}&page=1&pageSize=2");
        var second = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts?zoneId={zoneId}&page=2&pageSize=2");

        Assert.Equal(3, first.GetProperty("total").GetInt32());
        Assert.Equal(["R-03", "R-02"], first.GetProperty("items").EnumerateArray().Select(i => i.Text("code")));
        Assert.Equal(["R-01"], second.GetProperty("items").EnumerateArray().Select(i => i.Text("code")));
        var items = first.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("currentCycle").ValueKind);
        Assert.Equal(cycle.Id(), items[1].GetProperty("currentCycle").Id());
        Assert.Equal(start.ToString("yyyy-MM-dd"), items[1].GetProperty("currentCycle").Text("startDate"));
        Assert.Equal(5, items[1].GetProperty("laneCount").GetInt32());
    }

    [Fact]
    public async Task The_list_filters_by_zone_status_and_part_of_the_code()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var otherZoneId = (await owner.CreateZoneAsync()).Id();
        var stopped = await owner.CreateRaftAsync(zoneId, NewRaftBody("F-STOP"));
        await (await owner.PostAsJsonAsync($"/api/rafts/{stopped.Id()}/discontinue", new { rowVersion = stopped.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        await owner.CreateRaftAsync(zoneId, NewRaftBody("F-ALPHA"));
        await owner.CreateRaftAsync(otherZoneId, NewRaftBody("F-BETA"));

        async Task<List<string>> CodesAsync(string query) =>
            (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts?{query}")).GetProperty("items").EnumerateArray().Select(i => i.Text("code")).ToList();

        Assert.Equal(["F-STOP", "F-ALPHA"], await CodesAsync($"zoneId={zoneId}"));
        Assert.Equal(["F-ALPHA"], await CodesAsync($"zoneId={zoneId}&statusId={StatusIds.Usage.InUse}"));
        Assert.Equal(["F-ALPHA"], await CodesAsync($"zoneId={zoneId}&search=ALPH"));
        Assert.Contains("F-BETA", await CodesAsync($"search=F-"));
        Assert.Empty(await CodesAsync($"zoneId={otherZoneId}&search=ALPHA"));
    }

    [Fact]
    public async Task The_zone_list_shows_the_open_cycle_too()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raft = await owner.CreateRaftAsync(zoneId);
        var cycle = await owner.StartCycleAsync(raft.Id(), new CycleBody(Today.AddDays(-1)));
        await owner.CreateRaftAsync(zoneId);

        var list = (await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zoneId}/rafts")).EnumerateArray().ToList();

        Assert.Equal(cycle.Id(), list.Single(r => r.Id() == raft.Id()).GetProperty("currentCycle").Id());
        Assert.Single(list, r => r.GetProperty("currentCycle").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task A_zone_manager_reads_only_the_rafts_of_its_zones_and_an_unknown_zone_is_404()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await factory.AddZoneAsync(AcsTestKit.NewCode());
        var other = (await owner.CreateZoneAsync()).Id();
        await owner.CreateRaftAsync(zone.Id, NewRaftBody("M-OWN"));
        await owner.CreateRaftAsync(other, NewRaftBody("M-OTHER"));
        var (manager, _) = await WaterTestKit.StaffAsync(factory, zone);

        var own = await manager.GetFromJsonAsync<JsonElement>("/api/rafts");
        var forbidden = await manager.GetAsync($"/api/rafts?zoneId={other}");
        var missing = await owner.GetAsync($"/api/rafts?zoneId={Guid.NewGuid()}");

        Assert.Equal(["M-OWN"], own.GetProperty("items").EnumerateArray().Select(i => i.Text("code")));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
