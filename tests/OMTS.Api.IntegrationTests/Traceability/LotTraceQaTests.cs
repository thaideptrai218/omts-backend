using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>QA of TRACE-02 Trace lot (OMTS-222 T1b): backward completeness, prices per invoice zone, export cells, cycles and Removed lanes, correction marks.</summary>
public class LotTraceQaTests
{
    // TRACE-02.1 + BR-TRACE-03: the backward trace shows every seed lot hung on a lane; "another part of the backward trace is missing" covers a re-hung seed lot whose
    // purchase receipt is Cancelled (no seed lot by GBR-04). MSG-TRACE-02 must name it, even though the lane also has a seed lot of a Completed receipt.
    [Fact]
    public async Task A_re_hung_seed_lot_on_a_cancelled_receipt_is_named_by_msg_trace_02_even_when_the_lane_has_another_seed_lot()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var again = await owner.SeedLotAsync();
        await owner.HalveLaneAsync(raft.Session, 2);
        await owner.SeedingRecordAsync(raft.CycleId, again.Session([2], date: Today).Percents(50m, 2));
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2));
        await api.UpdateAsync<PurchaseReceipt>(again.ReceiptId, r => r.StatusId = StatusIds.PurchaseReceipt.Cancelled);

        var trace = await owner.TraceAsync(lot.Id());

        Assert.Equal([$"1: {raft.Lot.Code}", $"2: {raft.Lot.Code}, {again.Code}"], trace.LaneSeedLots());
        Assert.Equal($"SeedLotNotOnCompletedReceipt 2 {again.Code}", Assert.Single(trace.Missing()));
        Assert.Equal("MSG-TRACE-02", trace.Text("messageCode"));
        Assert.Contains(again.Code, trace.Text("message"));
    }

    // BR-SALE-08: a lot of zone A on an invoice of zone B. AC-11: the Zone Manager of A still sees the invoice and its trader, without prices (deviation 5), in the file too.
    // The Zone Manager of B only does not trace a lot of A (TRACE-02.0.E1); a Zone Manager at All zones sees everything and exports.
    [Fact]
    public async Task An_invoice_of_another_zone_is_listed_with_its_trader_but_without_prices_for_the_zone_manager_of_the_lot()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId, kg: 10m);
        var zoneB = (await sale.Owner.Client.CreateZoneAsync()).Id();
        await api.UpdateAsync<SalesInvoice>(invoice.Id(), i => i.ZoneId = zoneB);
        var managerB = await api.ManagerAsync(zoneB);
        var allZones = await api.StaffAsync(null, StaffRole.ZoneManager);

        var trace = await sale.Manager.Client.TraceAsync(sale.LotId);

        var listed = Assert.Single(trace.GetProperty("invoices").EnumerateArray());
        Assert.Equal((invoice.Text("code"), zoneB, sale.CustomerId, 10m), (listed.Text("invoiceCode"), listed.GetProperty("zoneId").GetGuid(),
            listed.GetProperty("customerId").GetGuid(), listed.GetProperty("kg").GetDecimal()));
        var line = Assert.Single(listed.GetProperty("lines").EnumerateArray());
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (line.GetProperty("unitPrice").ValueKind, line.GetProperty("amount").ValueKind));
        var forward = (await (await sale.Manager.Client.GetTraceExportAsync(sale.LotId)).SheetsAsync())["Forward"];
        Assert.Equal((invoice.Text("code"), "10", "", ""), (forward[1][0], forward[1][8], forward[1][9], forward[1][10]));

        Assert.Equal(HttpStatusCode.Forbidden, (await managerB.Client.GetTraceAsync(sale.LotId)).StatusCode);
        Assert.Empty(await managerB.Client.FoundAsync(invoiceCode: invoice.Text("code")));

        foreach (var client in new[] { allZones.Client, sale.Owner.Client })
        {
            var full = await client.TraceAsync(sale.LotId);
            Assert.Equal(ListPrice, full.GetProperty("invoices")[0].GetProperty("lines")[0].GetProperty("unitPrice").GetDecimal());
            Assert.True(full.GetProperty("canExport").GetBoolean());
            Assert.Equal([sale.LotId], await client.FoundAsync(invoiceCode: invoice.Text("code")));
            Assert.Equal(HttpStatusCode.OK, (await client.GetTraceExportAsync(sale.LotId)).StatusCode);
        }
    }

    // TRACE-02.2: text that looks like a formula stays text, times and dates are date cells, kg is a number, and a lot code with odd characters gives a safe file name.
    [Fact]
    public async Task The_export_keeps_formula_like_text_as_text_dates_as_dates_and_a_safe_file_name()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UpdateAsync<Customer>(sale.CustomerId, c => c.Name = "=1+2");
        await api.UpdateAsync<HarvestLot>(sale.LotId, l => l.LotCode = "KV/01 B:01*\"x");
        await sale.Manager.Client.SoldAsync(sale, sale.LotId, kg: 2.5m);

        var export = await sale.Manager.Client.GetTraceExportAsync(sale.LotId);

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var name = export.Content.Headers.ContentDisposition?.FileNameStar ?? export.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.Equal("trace-KV_01_B_01__x.xlsx", name);
        using var book = new XLWorkbook(await export.Content.ReadAsStreamAsync());
        var customer = book.Worksheet("Forward").Cell(2, 5);
        Assert.Equal((XLDataType.Text, "=1+2", false), (customer.DataType, customer.GetString(), customer.HasFormula));
        Assert.Equal(XLDataType.Number, book.Worksheet("Forward").Cell(2, 9).DataType);
        Assert.Equal(XLDataType.DateTime, book.Worksheet("Forward").Cell(2, 2).DataType);
        var lotRow = book.Worksheet("Lot");
        Assert.Equal((XLDataType.Text, "KV/01 B:01*\"x"), (lotRow.Cell(2, 1).DataType, lotRow.Cell(2, 1).GetString()));
        Assert.Equal((XLDataType.DateTime, XLDataType.DateTime), (lotRow.Cell(2, 2).DataType, lotRow.Cell(2, 3).DataType));
        Assert.Equal(XLDataType.DateTime, book.Worksheet("Backward").Cell(2, 3).DataType);
    }

    // BR-TRACE-03 "of the lot's cycle" with BR-FARM-04 "a removed lane keeps its records": after the close, lane 5 is Removed and the raft is seeded again in a new cycle.
    // The old lot still shows its own seed lot on lanes 4 and 5, never the seed lot of the new cycle, and misses nothing.
    [Fact]
    public async Task A_lot_of_a_closed_cycle_keeps_its_own_seed_lots_after_a_lane_is_removed_and_the_raft_is_seeded_again()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(4, 5));
        await api.UpdateAsync<CultivationCycle>(raft.CycleId, c => (c.StatusId, c.CloseDate) = (StatusIds.CultivationCycle.Closed, Today.AddDays(-2)));
        var lane5 = await api.QueryAsync(db => db.Lanes.Where(l => l.RaftId == raft.RaftId && l.LaneNumber == 5).Select(l => l.Id).SingleAsync());
        await api.UpdateAsync<Lane>(lane5, l => l.StatusId = StatusIds.Lane.Removed);
        var newCycle = (await owner.StartCycleAsync(raft.RaftId, new CycleBody(Today))).Id();
        var newSeed = await owner.SeedLotAsync();
        await owner.SeedingRecordAsync(newCycle, newSeed.Session([4], date: Today));

        var trace = await owner.TraceAsync(lot.Id());

        Assert.Equal([$"4: {raft.Lot.Code}", $"5: {raft.Lot.Code}"], trace.LaneSeedLots());
        Assert.Equal((raft.CycleId, "Closed", 0, JsonValueKind.Null), (trace.GetProperty("cycleId").GetGuid(), trace.Text("cycleStatus"),
            trace.GetProperty("missing").GetArrayLength(), trace.GetProperty("message").ValueKind));
        var backward = (await (await owner.GetTraceExportAsync(lot.Id())).SheetsAsync())["Backward"];
        Assert.Equal(["4", "5"], backward.Skip(1).Select(r => r[0]));
    }

    // TRACE-02.0 step 3: a TRACE-03 correction of another lot of the same cycle is marked on that lot only.
    [Fact]
    public async Task A_correction_of_another_lot_of_the_same_cycle_is_marked_on_that_lot_only()
    {
        using var api = new AuthApiFactory();
        var t = await api.TracedRaftAsync();
        var second = (await t.Owner.Client.HarvestLotAsync(t.RaftId, HarvestFields.OfLanes(2))).Id();
        await t.Owner.Client.RecordLogAsync(t.CycleId, new LogFields { Notes = "Buoys fine" });
        await t.Owner.Client.CloseCycleAsync(t.CycleId);
        (await api.SendLotTraceAsync(t.Owner.Client, second, HarvestFields.OfLanes(2) with { Cages = "7" }, reason: "DEMO cages recounted")).EnsureSuccessStatusCode();

        var first = await t.Manager.Client.TraceAsync(t.LotId);
        var corrected = await t.Manager.Client.TraceAsync(second);

        Assert.Equal(0, first.GetProperty("corrections").GetArrayLength());
        Assert.All(first.Seedings().Values, s => Assert.Equal(0, s.GetProperty("corrections").GetArrayLength()));
        var mark = Assert.Single(corrected.GetProperty("corrections").EnumerateArray());
        Assert.Equal(("DEMO cages recounted", t.Owner.Account.Id), (mark.Text("reason"), mark.GetProperty("actorAccountId").GetGuid()));
        Assert.All(corrected.Seedings().Values, s => Assert.Equal(0, s.GetProperty("corrections").GetArrayLength()));
    }
}
