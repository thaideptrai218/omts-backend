using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>QA of PR H1b: the lots for sale (SALE-05 step 1, SCR-SALE-04), the dashboard count (FARM-06, BR-FARM-10), and the answers of the routes as the client list gives them.</summary>
public class HarvestCorrectionsQaTestsForSale
{
    private static async Task<(SeededRaft Raft, Guid OtherZone, Guid OtherRaft, JsonElement LotHere, JsonElement LotThere)> TwoZonesAsync(AuthApiFactory api)
    {
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var otherZone = (await owner.CreateZoneAsync()).Id();
        var otherRaft = (await owner.CreateRaftAsync(otherZone)).Id();
        var otherCycle = (await owner.StartCycleAsync(otherRaft, new CycleBody(Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(otherCycle, raft.Lot.Session([1, 2]));
        return (raft, otherZone, otherRaft, await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1)), await owner.HarvestLotAsync(otherRaft, HarvestFields.OfLanes(1)));
    }

    private static async Task<List<string>> CodesAsync(HttpClient client, Guid? zoneId = null) => (await client.LotsForSaleAsync(zoneId)).ForSaleCodes();

    private static async Task AssertForbiddenAsync(HttpResponseMessage answer)
    {
        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Equal("MSG-GEN-08", await answer.ErrorCodeAsync());
    }

    // §I.4.4, SALE-05: Owner Full, Zone Manager Full*, others No. A pair of another role in the zone gives nothing, and a pair of a manager elsewhere gives only elsewhere.
    [Fact]
    public async Task Mixed_pairs_read_only_the_zones_of_their_manager_pairs()
    {
        using var api = new AuthApiFactory();
        var (raft, otherZone, _, here, there) = await TwoZonesAsync(api);
        var (mixed, _) = await api.StaffAccountAsync(await api.ZoneEntityAsync(raft.ZoneId));
        await api.GrantAsync(mixed, StaffRole.Accountant, await api.ZoneEntityAsync(otherZone));
        var client = await api.LoginAsync(mixed.Username);

        Assert.Equal([here.Text("lotCode")], await CodesAsync(client));
        await AssertForbiddenAsync(await client.GetAsync($"/api/harvest-lots/for-sale?zoneId={otherZone}"));

        var (allAccountant, _) = await api.StaffAccountAsync(await api.ZoneEntityAsync(raft.ZoneId));
        await api.GrantAsync(allAccountant, StaffRole.Accountant, null);
        var second = await api.LoginAsync(allAccountant.Username);
        Assert.Equal([here.Text("lotCode")], await CodesAsync(second));
        await AssertForbiddenAsync(await second.GetAsync($"/api/harvest-lots/for-sale?zoneId={otherZone}"));

        var (allManager, _) = await api.StaffAccountAsync(await api.ZoneEntityAsync(raft.ZoneId), StaffRole.Accountant);
        await api.GrantAsync(allManager, StaffRole.ZoneManager, null);
        var third = await api.LoginAsync(allManager.Username);
        Assert.Equal(new[] { here.Text("lotCode"), there.Text("lotCode") }.Order(), (await CodesAsync(third)).Order());
    }

    [Fact]
    public async Task A_revoked_pair_reads_nothing_from_the_next_request_on()
    {
        using var api = new AuthApiFactory();
        var (raft, _, _, _, _) = await TwoZonesAsync(api);
        var manager = await api.ManagerAsync(raft.ZoneId);
        Assert.Single(await CodesAsync(manager.Client));

        await api.UpdateAsync<AccessGrant>(manager.Grant!.Id, g => { g.StatusId = StatusIds.AccessGrant.Revoked; g.RevokedAt = DateTimeOffset.UtcNow; });

        await AssertForbiddenAsync(await manager.Client.GetAsync("/api/harvest-lots/for-sale"));
        await AssertForbiddenAsync(await manager.Client.GetAsync($"/api/harvest-lots/for-sale?zoneId={raft.ZoneId}"));
    }

    // BR-HARVEST-04: selling goes on after the close of the cycle; BR-CYCLE-04 locks cultivation records, not sales. A raft and then a zone discontinued keep their history, the lots for sale among it.
    [Fact]
    public async Task A_lot_for_sale_stays_on_the_list_and_the_count_when_its_cycle_closes_and_its_raft_and_zone_are_discontinued()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var manager = await api.ManagerAsync(raft.ZoneId);
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));

        await api.CloseCycleAsync(raft.CycleId);
        Assert.Equal([lot.Text("lotCode")], await CodesAsync(manager.Client));
        Assert.Equal(1, await owner.ForSaleCountAsync(raft.ZoneId));

        await api.DiscontinueRaftAsync(raft.RaftId);
        Assert.Equal([lot.Text("lotCode")], await CodesAsync(manager.Client, raft.ZoneId));
        Assert.Equal(1, await owner.ForSaleCountAsync());

        await api.UpdateAsync<FarmingZone>(raft.ZoneId, z => z.StatusId = StatusIds.Usage.Discontinued);
        Assert.Equal([lot.Text("lotCode")], await CodesAsync(manager.Client));
        Assert.Equal([lot.Text("lotCode")], await CodesAsync(owner, raft.ZoneId));
        Assert.Equal(1, await owner.ForSaleCountAsync(raft.ZoneId));
    }

    // MSG-GEN-01 is the code of a value that cannot be read: the same shape as every other refusal, whoever asks.
    [Theory]
    [InlineData("/api/harvest-lots/for-sale?zoneId=abc")]
    [InlineData("/api/harvest-lots/for-sale/count?zoneId=abc")]
    [InlineData("/api/harvest-lots/for-sale?zoneId=1")]
    public async Task A_malformed_zone_answers_400_with_msg_gen_01_like_every_other_refused_value(string url)
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();

        var answer = await raft.Owner.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal("MSG-GEN-01", await answer.ErrorCodeAsync());
    }

    // An empty, repeated or all-zeros zone: nothing leaks and nothing fails.
    [Fact]
    public async Task An_empty_all_zero_or_repeated_zone_neither_fails_nor_reaches_further()
    {
        using var api = new AuthApiFactory();
        var (raft, otherZone, _, here, _) = await TwoZonesAsync(api);
        var manager = await api.ManagerAsync(raft.ZoneId);

        Assert.Equal([here.Text("lotCode")], (await manager.Client.GetFromJsonElementAsync("/api/harvest-lots/for-sale?zoneId=")).ForSaleCodes());
        await AssertForbiddenAsync(await manager.Client.GetAsync($"/api/harvest-lots/for-sale?zoneId={Guid.Empty}"));
        await AssertForbiddenAsync(await manager.Client.GetAsync($"/api/harvest-lots/for-sale?zoneId={otherZone}&zoneId={raft.ZoneId}"));
        Assert.Equal([here.Text("lotCode")], (await manager.Client.GetFromJsonElementAsync($"/api/harvest-lots/for-sale?zoneId={raft.ZoneId}&zoneId={otherZone}")).ForSaleCodes());

        var owner = raft.Owner.Client;
        var none = await owner.LotsForSaleAsync(Guid.NewGuid());
        Assert.Equal(("MSG-GEN-10", 0), (none.Text("messageCode"), none.ForSaleCodes().Count));
        Assert.Equal(0, await owner.ForSaleCountAsync(Guid.Empty));
    }

    // SCR-SALE-04: a correction shows on the list at once; the remaining kg is worked out, a Published lot stays Published.
    [Fact]
    public async Task The_list_shows_a_correction_at_once_with_the_remaining_kg_worked_out_and_the_origin_state_kept()
    {
        using var api = new SalesDoubleApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        await api.UpdateAsync<HarvestLot>(lot.Id(), l => l.PublishedAt = DateTimeOffset.UtcNow);
        api.Sales.Figures[lot.Id()] = new OMTS.Application.Features.Harvest.LotSalesFigures(100m, 0m, 0m, 0m);

        await api.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Cages = "6" });

        var item = (await owner.LotsForSaleAsync(raft.ZoneId)).GetProperty("lots")[0];
        Assert.Equal((200m, "Published"), (item.Kg("remainingKg"), item.Text("originState")));
    }

    // The client list, section 1: the fields of the answer of a PUT are those of a record.
    [Fact]
    public async Task The_answer_of_a_correction_is_a_saved_lot_with_the_commands_the_message_and_the_new_cycle_version()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync(1);
        var manager = await api.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId);

        var saved = await api.CorrectLotAsync(manager.Client, lot.Id(), new HarvestFields { Cages = "9" });

        Assert.Equal(
            new[] { "cycleRowVersion", "everySeededLaneHarvested", "lot", "message", "messageCode", "warnings" },
            saved.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(("Harvest lot saved.", "MSG-GEN-05"), (saved.Text("message"), saved.Text("messageCode")));
        Assert.Equal((true, true), (saved.GetProperty("lot").Flag("canCorrect"), saved.GetProperty("lot").Flag("canRemove")));
    }

    // Malformed requests answer 4xx and change nothing.
    [Fact]
    public async Task Malformed_requests_on_the_routes_of_a_correction_answer_4xx_and_change_nothing()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var url = $"/api/harvest-lots/{lot.Id()}";

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await owner.PutAsync(url, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        var manyLanes = new MultipartFormDataContent();
        for (var i = 0; i < 1100; i++)
        {
            manyLanes.Add(new StringContent("1"), "laneNumbers");
        }

        var answers = new[]
        {
            await owner.PutAsync(url, new StringContent("not a form", System.Text.Encoding.UTF8, "multipart/form-data")),
            await owner.PutAsync(url, new MultipartFormDataContent()),
            await owner.PutAsync(url, manyLanes),
            await owner.PostAsync($"{url}/remove", new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json")),
            await owner.PostAsync($"{url}/remove", new StringContent("{", System.Text.Encoding.UTF8, "application/json")),
            await owner.PostAsync($"{url}/remove", new StringContent("{\"reason\":1,\"rowVersion\":2}", System.Text.Encoding.UTF8, "application/json")),
            await owner.PostAsync($"{url}/remove", new StringContent("[]", System.Text.Encoding.UTF8, "application/json")),
            await owner.PostAsync($"{url}/remove", new StringContent("{\"reason\":\"x\",\"rowVersion\":\"not base64\"}", System.Text.Encoding.UTF8, "application/json")),
            await owner.PostAsync($"{url}/remove", new StringContent("{\"reason\":\"x\"}", System.Text.Encoding.UTF8, "application/json")),
            await owner.PostAsync($"{url}/remove", new StringContent("{\"rowVersion\":\"AAAA\"}", System.Text.Encoding.UTF8, "application/json")),
        };

        foreach (var answer in answers)
        {
            Assert.InRange((int)answer.StatusCode, 400, 499);
        }

        Assert.Equal(1, await api.LotCountAsync(raft.RaftId));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/harvest-lots/not-a-guid")).StatusCode);
    }
}

file static class ForSaleQaCalls
{
    public static async Task<JsonElement> GetFromJsonElementAsync(this HttpClient client, string url) =>
        await (await client.GetAsync(url)).ReadAsync(HttpStatusCode.OK);
}
