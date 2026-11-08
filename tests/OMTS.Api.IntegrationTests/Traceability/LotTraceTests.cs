using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>TRACE-02 Trace lot: the forward trace (AC-11, BR-TRACE-03), the backward trace and its missing data (TRACE-02.1, MSG-TRACE-02), the search and the export (TRACE-02.2).</summary>
public class LotTraceTests
{
    // AC-11 with BR-TRACE-03: every invoice that sells the lot, a walk-in sale too, at its latest values, with its trader; a cancelled invoice is no sale. Sales returns come with
    // CLAIM-03: each invoice carries an empty list until then.
    [Fact]
    public async Task Ac_11_the_forward_trace_finds_every_invoice_that_sells_the_lot_and_leaves_out_a_cancelled_one()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var second = await Sales.SalesCatalogueTestKit.CreateCustomerAsync(sale.Owner.Client, name: "DEMO trader two");
        var first = await sale.Manager.Client.SoldAsync(sale, sale.LotId, kg: 10m);
        var walkIn = await sale.Manager.Client.SoldAsync(sale, sale.LotId, kg: 4m, walkIn: true);
        var cancelled = await sale.Manager.Client.SoldAsync(sale, sale.LotId, kg: 3m, customerId: second.Id());
        var last = await sale.Manager.Client.SoldAsync(sale, sale.LotId, kg: 2m, customerId: second.Id());
        await sale.Manager.Client.SoldAsync(sale, sale.OtherLotId, kg: 5m);
        await api.CancelInvoiceAsync(cancelled.Id());

        var trace = await sale.Manager.Client.TraceAsync(sale.LotId);

        Assert.Equal([first.Text("code"), walkIn.Text("code"), last.Text("code")], trace.InvoiceCodes());
        var invoices = trace.GetProperty("invoices").EnumerateArray().ToList();
        Assert.Equal([(sale.CustomerId, false), (Guid.Empty, true), (second.Id(), false)],
            invoices.Select(i => (i.GetProperty("customerId").ValueKind == JsonValueKind.Null ? Guid.Empty : i.GetProperty("customerId").GetGuid(), i.GetProperty("walkIn").GetBoolean())));
        Assert.Equal(("DEMO trader two", second.Text("phoneNumber")), (invoices[2].Text("customerName"), invoices[2].Text("customerPhone")));
        Assert.Equal(16m, trace.GetProperty("invoicedKg").GetDecimal());
        var line = Assert.Single(invoices[0].GetProperty("lines").EnumerateArray());
        Assert.Equal((10m, ListPrice, 10m * ListPrice), (line.GetProperty("kg").GetDecimal(), line.GetProperty("unitPrice").GetDecimal(), line.GetProperty("amount").GetDecimal()));
        Assert.All(invoices, i => Assert.Equal(0, i.GetProperty("returns").GetArrayLength()));
        Assert.Equal((sale.ZoneId, "Open", "NotPublished", true), (invoices[0].GetProperty("zoneId").GetGuid(), trace.Text("status"), trace.Text("originState"),
            trace.GetProperty("canPublish").GetBoolean()));

        // Step 1: the lot through any invoice that sells it, the cancelled one too (it keeps its lines), or by its code.
        foreach (var invoice in new[] { first, cancelled, last })
        {
            Assert.Equal([sale.LotId], await sale.Manager.Client.FoundAsync(invoiceCode: invoice.Text("code")));
        }

        // The other lot of the day has the code with a suffix (-2): it starts with this code, and comes after the lot of exactly this code.
        Assert.Equal([sale.LotId, sale.OtherLotId], await sale.Manager.Client.FoundAsync(lotCode: trace.Text("lotCode")));
    }

    // BR-TRACE-03 with TRACE-02.1: lane 2 hung again shows both seed lots with their receipts and suppliers; lane 3 lost its session, so the trace names it (MSG-TRACE-02) and
    // chooses nothing in its place; a seed lot of a cancelled receipt is shown as such.
    [Fact]
    public async Task The_backward_trace_shows_each_seed_lot_of_each_lane_and_msg_trace_02_names_what_is_missing()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var again = await owner.SeedLotAsync();
        var rehung = await owner.SeedingRecordAsync(raft.CycleId, again.Session([2], date: Today));
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2, 3).FinishedLanes(1));
        await api.LeaveLaneWithoutSeedLotAsync(raft.RaftId, 3);
        await api.UpdateAsync<PurchaseReceipt>(again.ReceiptId, r => r.StatusId = StatusIds.PurchaseReceipt.Cancelled);

        var trace = await owner.TraceAsync(lot.Id());

        Assert.Equal([$"1: {raft.Lot.Code}", $"2: {raft.Lot.Code}, {again.Code}", "3: "], trace.LaneSeedLots());
        Assert.True(trace.GetProperty("lanes")[0].GetProperty("isFinished").GetBoolean());
        var session = trace.Seedings()[rehung.Id()];
        Assert.Equal((again.SupplierId, again.ReceiptId, false, "Cancelled"), (session.GetProperty("seedLot").GetProperty("supplierId").GetGuid(),
            session.GetProperty("seedLot").GetProperty("receiptId").GetGuid(), session.GetProperty("onCompletedReceipt").GetBoolean(), session.Text("receiptStatus")));
        Assert.Equal((raft.RaftId, raft.ZoneId, raft.CycleId, "Open"), (trace.GetProperty("raftId").GetGuid(), trace.GetProperty("zoneId").GetGuid(),
            trace.GetProperty("cycleId").GetGuid(), trace.Text("cycleStatus")));
        // Every seed lot of a lane is traced (BR-TRACE-03): the re-hung seed lot on a cancelled receipt is named although lane 2 has a valid one.
        Assert.Equal([$"SeedLotNotOnCompletedReceipt 2 {again.Code}", "NoSeedingSession 3"], trace.Missing());
        Assert.Equal("MSG-TRACE-02", trace.Text("messageCode"));
        Assert.StartsWith($"Missing data in the trace of harvest lot {lot.Text("lotCode")}: ", trace.Text("message"));
        Assert.Contains("lane 3 has no seeding session in the cycle of the lot", trace.Text("message"));
        Assert.Contains(again.Code, trace.Text("message"));
        Assert.Empty(trace.GetProperty("invoices").EnumerateArray());
        Assert.False(trace.GetProperty("canPublish").GetBoolean());

        // A complete trace has no message.
        var complete = await owner.TraceAsync((await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(4))).Id());
        Assert.Equal((JsonValueKind.Null, 0), (complete.GetProperty("message").ValueKind, complete.GetProperty("missing").GetArrayLength()));
    }

    // TRACE-02.2: the trace shown, in a file of three sheets; the trace stays as it was.
    [Fact]
    public async Task The_export_holds_the_lot_the_backward_and_the_forward_trace()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId, kg: 10m);
        await sale.Manager.Client.SoldAsync(sale, sale.LotId, kg: 4m, walkIn: true);
        var trace = await sale.Manager.Client.TraceAsync(sale.LotId);
        var code = trace.Text("lotCode");

        var export = await sale.Manager.Client.GetTraceExportAsync(sale.LotId);

        Assert.Equal((HttpStatusCode.OK, LotTraceTestKit.XlsxType, $"trace-{code}.xlsx"),
            (export.StatusCode, export.Content.Headers.ContentType?.MediaType, export.Content.Headers.ContentDisposition?.FileNameStar ?? export.Content.Headers.ContentDisposition?.FileName));
        var sheets = await export.SheetsAsync();
        Assert.Equal(["Lot", "Backward", "Forward"], sheets.Keys);
        Assert.Equal(["Lot code", "Harvested at", "Harvest date", "Zone", "Raft"], sheets["Lot"][0].Take(5));
        Assert.Equal((code, trace.Text("raftCode"), "1"), (sheets["Lot"][1][0], sheets["Lot"][1][4], sheets["Lot"][1][8]));
        var seedLot = trace.GetProperty("lanes")[0].GetProperty("seedings")[0].GetProperty("seedLot");
        Assert.Equal(("1", seedLot.Text("code"), seedLot.Text("receiptCode"), seedLot.Text("supplierName")),
            (sheets["Backward"][1][0], sheets["Backward"][1][5], sheets["Backward"][1][7], sheets["Backward"][1][10]));
        Assert.Equal(2, sheets["Backward"].Count);
        Assert.Equal(3, sheets["Forward"].Count);
        var product = trace.GetProperty("invoices")[0].GetProperty("lines")[0].Text("productName");
        Assert.Equal((invoice.Text("code"), product, "10"), (sheets["Forward"][1][0], sheets["Forward"][1][7], sheets["Forward"][1][8]));
        Assert.Equal("Walk-in customer", sheets["Forward"][2][4]);
        Assert.Equal(trace.GetRawText(), (await sale.Manager.Client.TraceAsync(sale.LotId)).GetRawText());
    }
}
