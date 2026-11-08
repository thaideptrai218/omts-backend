using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Harvest.HarvestYieldTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// QA of HARVEST-04 (OMTS-221 H3) on the figures and the selection: cancelled invoices (BR-SALE-11, BR-HARVEST-07), a lot on several invoices with 3-decimal kg,
/// removed lots (BR-HARVEST-05), the Not final mark per group (MSG-HARVEST-04), mixed zone-role pairs and discontinued zones (BR-FARM-08).
/// </summary>
public class HarvestYieldQaTests
{
    // BR-HARVEST-07 / BR-SALE-11: a cancelled invoice counts in no yield figure; the kg of the other invoices of the lot add up, to the gram.
    [Fact]
    public async Task A_lot_on_several_invoices_sums_their_kg_exactly_and_a_cancelled_invoice_never_counts()
    {
        using var api = new AuthApiFactory();
        var setup = await api.YieldSetupAsync();
        var lot = (await setup.Owner.HarvestLotAsync(setup.Raft.RaftId)).Id();
        await setup.Owner.SellKgAsync(setup, lot, 100.125m, soldWholeLot: false);
        await setup.Owner.SellKgAsync(setup, lot, 33.333m, soldWholeLot: false);
        await setup.Owner.SellKgAsync(setup, lot, 66.667m, soldWholeLot: false);
        var invoices = await api.QueryAsync(db => db.SalesInvoiceLines.Where(l => l.HarvestLotId == lot).OrderBy(l => l.Kg).Select(l => l.InvoiceId).ToListAsync());
        // The 66.667 kg invoice is cancelled (SALE-08 is not built: the status is set as SALE-08 will leave it).
        await api.UpdateAsync<SalesInvoice>(invoices[1], i => i.StatusId = StatusIds.SalesInvoice.Cancelled);

        var report = await setup.Owner.YieldAsync();

        var row = report.YieldLot(lot);
        Assert.Equal((133.458m, 0m, 133.458m), (row.Kg("invoicedKg"), row.Kg("returnedKg"), row.Kg("soldKg")));
        Assert.Equal((400m, 133.458m, 0m, 266.542m, true), row.Figures());
        Assert.Equal((400m, 133.458m, 0m, 266.542m, true), report.GetProperty("totals").Figures());
    }

    // H1b: a removed lot stays only in the audit log, so it is in no group and no total.
    [Fact]
    public async Task A_removed_lot_is_in_no_group_and_no_total()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var kept = (await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1))).Id();
        var removed = (await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2))).Id();
        await api.RemoveLotAsync(owner, removed);

        foreach (var groupBy in new[] { "raft", "zone", "cycle" })
        {
            var report = await owner.YieldAsync(YieldFilters(groupBy: groupBy));
            Assert.Equal([kept], report.YieldLotIds());
            Assert.Equal((1, 400m), (report.GetProperty("totals").GetProperty("lotCount").GetInt32(), report.GetProperty("totals").Kg("estimatedKg")));
        }
    }

    // HARVEST-04 step 3: a group is Not final while one of its lots is for sale (Reopened too); a group whose lots are all Closed stays final; the totals and MSG-HARVEST-04 follow.
    [Fact]
    public async Task Only_the_group_with_a_reopened_lot_is_not_final_and_the_notice_is_shown_once()
    {
        using var api = new AuthApiFactory();
        var raftA = await api.SeededRaftAsync();
        var owner = raftA.Owner.Client;
        var raftB = (await owner.CreateRaftAsync(raftA.ZoneId)).Id();
        var cycleB = (await owner.StartCycleAsync(raftB, new CycleBody(FarmingTestKit.Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleB, raftA.Lot.Session([1, 2]));
        var closedA = (await owner.HarvestLotAsync(raftA.RaftId)).Id();
        var reopenedB = (await owner.HarvestLotAsync(raftB)).Id();
        await api.CloseLotAsync(owner, closedA, 10m, "Spoiled");
        await api.CloseLotAsync(owner, reopenedB, 0m);
        await api.ReopenAsync(reopenedB, FarmingTestKit.Today);

        var report = await owner.YieldAsync();

        Assert.False(report.Group(raftA.RaftId).GetProperty("notFinal").GetBoolean());
        Assert.True(report.Group(raftB).GetProperty("notFinal").GetBoolean());
        Assert.Equal(("Reopened", true), (report.YieldLot(reopenedB).Text("status"), report.YieldLot(reopenedB).GetProperty("notFinal").GetBoolean()));
        Assert.Equal((800m, 0m, 10m, 790m, true), report.GetProperty("totals").Figures());
        Assert.Equal("MSG-HARVEST-04", report.Text("notFinalNoticeCode"));
        var byZone = Assert.Single((await owner.YieldAsync(YieldFilters(groupBy: "zone"))).Groups());
        Assert.True(byZone.GetProperty("notFinal").GetBoolean());
    }

    // BR-FARM-08: each role is limited to the zones of its own pairs. A Zone Manager pair in zone A and an Accountant pair in zone B give the yield of zone A only.
    [Fact]
    public async Task A_zone_manager_with_an_accountant_pair_in_another_zone_reads_and_exports_only_their_zone_manager_zone()
    {
        using var api = new AuthApiFactory();
        var raftA = await api.SeededRaftAsync();
        var owner = raftA.Owner.Client;
        var zoneB = (await owner.CreateZoneAsync()).Id();
        var raftB = (await owner.CreateRaftAsync(zoneB)).Id();
        var cycleB = (await owner.StartCycleAsync(raftB, new CycleBody(FarmingTestKit.Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleB, raftA.Lot.Session([1, 2]));
        var lotA = (await owner.HarvestLotAsync(raftA.RaftId)).Id();
        await owner.HarvestLotAsync(raftB);
        var (mixed, _) = await EmployeesV2QaKit.StaffAsync(
            api, (StaffRole.ZoneManager, await api.ZoneEntityAsync(raftA.ZoneId)), (StaffRole.Accountant, await api.ZoneEntityAsync(zoneB)));

        foreach (var groupBy in new[] { "raft", "zone", "cycle" })
        {
            var report = await mixed.YieldAsync(YieldFilters(groupBy: groupBy));
            Assert.Equal([lotA], report.YieldLotIds());
            Assert.Equal(400m, report.GetProperty("totals").Kg("estimatedKg"));
        }

        Assert.Equal("MSG-GEN-08", await (await mixed.GetYieldAsync(YieldFilters(zoneIds: [zoneB]))).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await mixed.GetYieldExportAsync(YieldFilters(zoneIds: [zoneB]))).ErrorCodeAsync());
        var file = await mixed.GetYieldExportAsync(YieldFilters(groupBy: "zone"));
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        using var book = new ClosedXML.Excel.XLWorkbook(await file.Content.ReadAsStreamAsync());
        Assert.Equal(2, book.Worksheet("Lots").RowsUsed().Count());
        Assert.Equal(400m, book.Worksheet("Groups").Row(3).Cell(4).GetValue<decimal>());
    }

    // BR-FARM-08 / GBR-01: the pairs of a discontinued zone stay Effective so their holders can still view its history; a discontinued raft keeps its lots.
    [Fact]
    public async Task The_lots_of_a_discontinued_zone_and_raft_still_count_for_its_zone_manager()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var lot = (await raft.Owner.Client.HarvestLotAsync(raft.RaftId)).Id();
        await api.CloseLotAsync(raft.Owner.Client, lot, 0m);
        var manager = (await api.ManagerAsync(raft.ZoneId)).Client;
        await api.UpdateAsync<Raft>(raft.RaftId, r => r.StatusId = StatusIds.Usage.Discontinued);
        await api.UpdateAsync<FarmingZone>(raft.ZoneId, z => z.StatusId = StatusIds.Usage.Discontinued);

        var report = await manager.YieldAsync(YieldFilters(zoneIds: [raft.ZoneId]));

        Assert.Equal([lot], report.YieldLotIds());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("messageCode").ValueKind);
    }
}
