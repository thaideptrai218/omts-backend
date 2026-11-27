using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>The nursery list for the zones chosen in the header: <c>zoneIds</c> is repeated, the zones the caller may not read are left out, and none left is an empty page, never every zone.</summary>
public sealed class NurseryZoneIdsTests
{
    private static HashSet<Guid> ZonesOf(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("zoneId").GetGuid()).ToHashSet();

    [Fact]
    public async Task Repeated_zone_ids_list_the_batches_of_those_zones_only()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var zoneB = (await client.CreateZoneAsync()).Id();
        var zoneC = (await client.CreateZoneAsync()).Id();
        foreach (var zone in new[] { world.ZoneId, zoneB, zoneC })
        {
            await client.StartNurseryAsync(zone, world.Lot, "100");
        }

        var two = await client.GetFromJsonAsync<JsonElement>($"/api/nursery-batches?zoneIds={world.ZoneId}&zoneIds={zoneB}");
        var joined = await client.GetFromJsonAsync<JsonElement>($"/api/nursery-batches?zoneId={zoneC}&zoneIds={world.ZoneId}");
        var every = await client.GetFromJsonAsync<JsonElement>("/api/nursery-batches");

        Assert.Equal(new HashSet<Guid> { world.ZoneId, zoneB }, ZonesOf(two));
        Assert.Equal(new HashSet<Guid> { world.ZoneId, zoneC }, ZonesOf(joined));
        Assert.Equal(3, every.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_zone_manager_loses_the_zones_they_may_not_read_without_a_refusal_and_never_gets_every_zone()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var otherZone = (await client.CreateZoneAsync()).Id();
        await client.StartNurseryAsync(world.ZoneId, world.Lot, "100");
        await client.StartNurseryAsync(otherZone, world.Lot, "100");
        var manager = await api.ManagerAsync(world.ZoneId);

        var mixed = await manager.Client.GetFromJsonAsync<JsonElement>($"/api/nursery-batches?zoneIds={world.ZoneId}&zoneIds={otherZone}");
        var foreign = await manager.Client.GetAsync($"/api/nursery-batches?zoneIds={otherZone}");
        var legacy = await manager.Client.GetAsync($"/api/nursery-batches?zoneId={otherZone}");

        Assert.Equal(new HashSet<Guid> { world.ZoneId }, ZonesOf(mixed));
        Assert.Equal(HttpStatusCode.OK, foreign.StatusCode);
        Assert.Equal(0, (await foreign.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, legacy.StatusCode);
    }

    [Fact]
    public async Task More_than_two_hundred_zone_ids_are_a_validation_error()
    {
        using var api = new AuthApiFactory();
        var owner = await api.OwnerAsync();
        var query = string.Join('&', Enumerable.Range(0, 201).Select(_ => $"zoneIds={Guid.NewGuid()}"));

        var response = await owner.Client.GetAsync($"/api/nursery-batches?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
