using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// The raft picker of the purchase receipt filters (PURCHASE-03): id and codes only, and it follows PURCHASE_RECEIPTS View.
/// It is how an Accountant, refused the raft reads (§I.4.4 FARM-04 No), fills the raft filter.
/// </summary>
public class PurchaseReceiptRaftLookupTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Lookup = "/api/purchase-receipts/raft-lookup";

    private async Task<(HttpClient Owner, JsonElement ZoneA, JsonElement ZoneB, Guid RaftA, Guid RaftB)> ArrangeAsync()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var (zoneA, zoneB) = (await owner.CreateZoneAsync(), await owner.CreateZoneAsync());
        return (owner, zoneA, zoneB, (await owner.CreateRaftAsync(zoneA.Id())).Id(), (await owner.CreateRaftAsync(zoneB.Id())).Id());
    }

    private static async Task<List<Guid>> IdsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>(Lookup)).EnumerateArray().Select(r => r.Id()).ToList();

    [Fact]
    public async Task The_lookup_holds_only_the_id_and_the_codes_of_the_raft_and_its_zone()
    {
        var (owner, zoneA, _, raftA, _) = await ArrangeAsync();
        await owner.AddRaftPhotoAsync(raftA);

        var all = (await owner.GetFromJsonAsync<JsonElement>(Lookup)).EnumerateArray().ToList();

        Assert.All(all, r => Assert.Equal(["code", "id", "zoneCode", "zoneId"], r.EnumerateObject().Select(p => p.Name).Order()));
        var found = all.Single(r => r.Id() == raftA);
        var saved = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftA}");
        Assert.Equal((saved.Text("code"), zoneA.Text("code")), (found.Text("code"), found.Text("zoneCode")));
    }

    // Old receipts name Discontinued rafts, also in a Discontinued zone, so the filter still offers them.
    [Fact]
    public async Task The_lookup_includes_a_discontinued_raft_of_a_discontinued_zone()
    {
        var (owner, zoneA, _, raftA, _) = await ArrangeAsync();
        var raft = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftA}");
        await (await owner.PostAsJsonAsync($"/api/rafts/{raftA}/discontinue", new { rowVersion = raft.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        var zone = await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zoneA.Id()}");
        await (await owner.PostAsJsonAsync($"/api/zones/{zoneA.Id()}/discontinue", new { rowVersion = zone.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);

        Assert.Contains(raftA, await IdsAsync(owner));
    }

    // Receipts have no zone and follow the role only (BR-FARM-08): a pair in one zone sees the receipts of every raft, so the
    // filter offers every raft too, or a receipt on the list could not be filtered by its own raft.
    [Theory]
    [InlineData(StaffRole.ZoneManager)]
    [InlineData(StaffRole.Accountant)]
    public async Task Staff_of_one_zone_and_staff_of_all_zones_get_every_raft(StaffRole role)
    {
        var (_, zoneA, _, raftA, raftB) = await ArrangeAsync();

        var ofZone = await IdsAsync((await factory.StaffAsync(await factory.ZoneEntityAsync(zoneA.Id()), role)).Client);
        Assert.Contains(raftA, ofZone);
        Assert.Contains(raftB, ofZone);

        var everywhere = await IdsAsync((await factory.StaffAsync(null, role)).Client);
        Assert.Contains(raftA, everywhere);
        Assert.Contains(raftB, everywhere);
    }

    [Fact]
    public async Task The_owner_gets_every_raft()
    {
        var (owner, _, _, raftA, raftB) = await ArrangeAsync();

        var all = await IdsAsync(owner);

        Assert.Contains(raftA, all);
        Assert.Contains(raftB, all);
    }

    // The picked raft works in the filters of the same caller: the point of the lookup.
    [Fact]
    public async Task An_accountant_filters_receipts_and_costs_by_a_raft_of_the_lookup_but_cannot_read_that_raft()
    {
        var (_, zoneA, _, raftA, _) = await ArrangeAsync();
        var accountant = (await factory.StaffAsync(await factory.ZoneEntityAsync(zoneA.Id()), StaffRole.Accountant)).Client;

        var picked = Assert.Single(await IdsAsync(accountant), id => id == raftA);

        await (await accountant.GetAsync($"/api/purchase-receipts?raftId={picked}")).ReadAsync(HttpStatusCode.OK);
        await (await accountant.GetAsync($"/api/purchase-receipts/raft-material-costs?raftId={picked}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("MSG-GEN-08", await (await accountant.GetAsync($"/api/rafts/{picked}")).ErrorCodeAsync());
    }

    [Fact]
    public async Task Anonymous_gets_401_and_a_trader_msg_gen_08()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Lookup)).StatusCode);

        var refused = await (await factory.TraderAsync()).GetAsync(Lookup);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("MSG-GEN-08", await refused.ErrorCodeAsync());
    }
}
