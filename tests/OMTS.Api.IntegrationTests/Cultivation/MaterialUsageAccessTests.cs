using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>§I.4.4 on every route: CYCLE-03 is Full for the Owner, Full within the assigned zones for a Zone Manager, and No for everyone else.</summary>
public class MaterialUsageAccessTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static IEnumerable<Func<Task<HttpResponseMessage>>> EveryRoute(HttpClient client, Guid raftId, Guid itemId, JsonElement record) =>
    [
        () => client.GetAsync($"/api/rafts/{raftId}/material-usages"),
        () => client.PostUsageAsync(raftId, new UsageBody(Today, "no access", [new UsageLine(itemId)])),
        () => client.GetAsync($"/api/material-usages/{record.Id()}"),
        () => client.PutUsageAsync(record.Id(), record.AsEdit() with { Quantity = 9m }),
    ];

    [Fact]
    public async Task Anonymous_gets_401_everywhere()
    {
        var setup = await factory.CycleAsync();
        var item = await factory.ItemAsync();
        var record = await setup.Owner.Client.RecordUsageAsync(setup.RaftId, item);

        foreach (var call in EveryRoute(factory.CreateClient(), setup.RaftId, item, record))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await call()).StatusCode);
        }
    }

    [Theory]
    [InlineData(StaffRole.Accountant, true)]
    [InlineData(StaffRole.Accountant, false)]
    [InlineData(StaffRole.ZoneManager, false)]
    public async Task An_accountant_and_a_zone_manager_of_another_zone_get_msg_gen_08_everywhere(StaffRole role, bool allZones)
    {
        var setup = await factory.CycleAsync();
        var item = await factory.ItemAsync();
        var record = await setup.Owner.Client.RecordUsageAsync(setup.RaftId, item);
        var zone = role == StaffRole.Accountant
            ? (allZones ? null : await factory.ZoneEntityAsync(setup.ZoneId))
            : await factory.ZoneEntityAsync((await setup.Owner.Client.CreateZoneAsync()).Id());
        var refused = await factory.StaffAsync(zone, role);

        foreach (var call in EveryRoute(refused.Client, setup.RaftId, item, record))
        {
            var answer = await call();
            Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
            Assert.Equal("MSG-GEN-08", await answer.ErrorCodeAsync());
        }

        Assert.Equal(2m, (await setup.Owner.Client.GetUsageAsync(record.Id())).GetProperty("quantity").GetDecimal());
    }

    [Fact]
    public async Task A_trader_gets_msg_gen_08()
    {
        var setup = await factory.CycleAsync();
        var trader = await factory.TraderAsync();
        var answer = await trader.GetAsync($"/api/rafts/{setup.RaftId}/material-usages");
        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
    }

    [Fact]
    public async Task A_zone_manager_of_the_zone_and_of_all_zones_and_the_owner_work_on_every_route()
    {
        var setup = await factory.CycleAsync();
        var item = await factory.ItemAsync();
        foreach (var client in new[] { (await factory.ManagerAsync(setup.ZoneId)).Client, (await factory.StaffAsync(null)).Client, setup.Owner.Client })
        {
            var record = await client.RecordUsageAsync(setup.RaftId, item);
            Assert.True((await client.UsageListAsync(setup.RaftId)).Flag("canRecord"));
            Assert.True((await client.GetUsageAsync(record.Id())).Flag("canEdit"));
            await (await client.PutUsageAsync(record.Id(), record.AsEdit() with { Quantity = 7m })).ReadAsync(HttpStatusCode.OK);
        }
    }

    // CYCLE-03.0 step 3: the screen lists the Material items from the catalogue, whose reads a Zone Manager passes through CULTIVATION_CYCLES.
    [Fact]
    public async Task A_zone_manager_can_read_the_material_items_to_choose_from()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var (material, discontinued, seed) = (await factory.ItemAsync(), await factory.ItemAsync(statusId: StatusIds.Usage.Discontinued), await factory.ItemAsync(InputItemGroup.Seed));

        var listed = await manager.Client.GetFromJsonAsync<JsonElement>($"/api/farming-input-items?group={MaterialUsage.ItemGroup}&statusId={StatusIds.Usage.InUse}");

        var ids = listed.EnumerateArray().Select(i => i.Id()).ToList();
        Assert.Contains(material, ids);
        Assert.DoesNotContain(discontinued, ids);
        Assert.DoesNotContain(seed, ids);
    }
}
