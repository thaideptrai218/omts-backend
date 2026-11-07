using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Cultivation;

using OMTS.Api.IntegrationTests.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

/// <summary>CYCLE-02.0 step 3 and GBR-04: the seed lots to choose from, those bought for this raft first, then the others newest first.</summary>
public class SeedLotListTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static List<Guid> Ids(JsonElement list) => list.GetProperty("lots").EnumerateArray().Select(l => l.Id()).ToList();

    [Fact]
    public async Task A_lot_shows_its_item_supplier_date_and_quantities_and_no_price()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(quantity: 80m, raftId: setup.RaftId);
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session(quantity: 30m));

        var list = await owner.SeedLotsAsync(setup.RaftId);

        Assert.Equal(setup.RaftId, list.GetProperty("raftId").GetGuid());
        Assert.True(list.IsNull("messageCode"));
        var offered = list.GetProperty("lots").EnumerateArray().Single(l => l.Id() == lot.LineId);
        Assert.Equal((lot.Code, lot.ReceiptId, lot.ItemId, lot.SupplierId, Today.AddDays(-10)), (offered.Text("code"), offered.GetProperty("receiptId").GetGuid(), offered.GetProperty("itemId").GetGuid(), offered.GetProperty("supplierId").GetGuid(), offered.Date("receivedDate")));
        Assert.Equal((80m, 30m, "kg", true), (offered.GetProperty("quantityReceived").GetDecimal(), offered.GetProperty("quantitySeeded").GetDecimal(), offered.Text("unit"), offered.Flag("boughtForThisRaft")));
        Assert.DoesNotContain(offered.EnumerateObject(), p => p.Name.Contains("price", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("amount", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Lots_bought_for_the_raft_come_first_then_the_others_newest_first()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var other = (await owner.CreateRaftAsync(setup.ZoneId)).Id();
        var older = await owner.SeedLotAsync(received: Today.AddDays(-9));
        var newer = await owner.SeedLotAsync(received: Today.AddDays(-2));
        var forOtherRaft = await owner.SeedLotAsync(received: Today.AddDays(-1), raftId: other);
        var ownOld = await owner.SeedLotAsync(received: Today.AddDays(-20), raftId: setup.RaftId);
        var ownNew = await owner.SeedLotAsync(received: Today.AddDays(-15), raftId: setup.RaftId);

        var ids = Ids(await owner.SeedLotsAsync(setup.RaftId));

        var position = new[] { ownNew, ownOld, forOtherRaft, newer, older }.Select(l => ids.IndexOf(l.LineId)).ToList();
        Assert.Equal(position.Order(), position);
        Assert.All(position, p => Assert.True(p >= 0));
        Assert.Equal([0, 1], position.Take(2));
    }

    [Fact]
    public async Task Drafts_cancelled_receipts_other_groups_and_lots_returned_in_whole_are_not_offered()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var draft = await owner.SeedLotAsync(complete: false);
        var cancelled = await owner.SeedLotAsync();
        await (await owner.CancelAsync(cancelled.ReceiptId, "Wrong supplier")).ReadAsync(HttpStatusCode.OK);
        var material = await owner.CreateItemAsync("Material", "piece");
        var materials = await owner.CreateReceiptAsync(new ReceiptBody { SupplierId = await owner.CreateSupplierAsync(), Lines = [new LineBody(material, 1m, 1m)], Complete = true });
        var whole = await owner.SeedLotAsync(quantity: 5m);
        var part = await owner.SeedLotAsync(quantity: 5m);
        await owner.ReturnLotAsync(whole, 5m);
        await owner.ReturnLotAsync(part, 4m);

        // The SRS hides a lot returned in whole, not one that is seeded in whole: seeding does not compare with what is left.
        var seededWhole = await owner.SeedLotAsync(quantity: 5m);
        await owner.RecordSeedingAsync(setup.CycleId, seededWhole.Session(quantity: 5m));

        var ids = Ids(await owner.SeedLotsAsync(setup.RaftId));

        Assert.Empty(new[] { draft.LineId, cancelled.LineId, materials.LineOf(material), whole.LineId }.Intersect(ids));
        Assert.Contains(part.LineId, ids);
        Assert.Contains(seededWhole.LineId, ids);
    }

    // No zone on a receipt (BR-FARM-08): a Zone Manager chooses any seed lot, as GBR-04 lists the others after those of the raft.
    [Fact]
    public async Task A_zone_manager_lists_every_lot_and_an_accountant_or_another_zone_is_refused()
    {
        var setup = await factory.CycleAsync();
        var lot = await setup.Owner.Client.SeedLotAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var outside = await factory.ManagerAsync((await setup.Owner.Client.CreateZoneAsync()).Id());
        var accountant = await factory.StaffAsync(await factory.ZoneEntityAsync(setup.ZoneId), StaffRole.Accountant);
        var path = $"/api/rafts/{setup.RaftId}/seed-lots";

        Assert.Contains(lot.LineId, Ids(await manager.Client.SeedLotsAsync(setup.RaftId)));
        Assert.Equal("MSG-GEN-08", await (await outside.Client.GetAsync(path)).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await accountant.Client.GetAsync(path)).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Owner.Client.GetAsync($"/api/rafts/{Guid.NewGuid()}/seed-lots")).StatusCode);
    }
}

/// <summary>CYCLE-02.0.E1, on a database of its own: with no seed lot yet the list says so (MSG-CYCLE-03).</summary>
public class SeedLotEmptyListTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task With_no_lot_to_choose_the_list_is_empty_with_msg_cycle_03()
    {
        var setup = await factory.CycleAsync();

        var list = await setup.Owner.Client.SeedLotsAsync(setup.RaftId);

        Assert.Empty(list.GetProperty("lots").EnumerateArray());
        Assert.Equal("MSG-CYCLE-03", list.Text("messageCode"));
        Assert.Equal("There is no seed lot to choose: no completed purchase receipt for seed exists yet. Please complete the purchase receipt for the seed first.", list.Text("message"));

        var lot = await setup.Owner.Client.SeedLotAsync(complete: false);
        Assert.Equal("MSG-CYCLE-03", (await setup.Owner.Client.SeedLotsAsync(setup.RaftId)).Text("messageCode"));

        await (await setup.Owner.Client.CompleteAsync(lot.ReceiptId)).ReadAsync(HttpStatusCode.OK);
        Assert.True((await setup.Owner.Client.SeedLotsAsync(setup.RaftId)).IsNull("messageCode"));
    }
}
