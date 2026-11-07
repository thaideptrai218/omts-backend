using System.Net;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>§I.4.4 TRACE-02 (Owner Full, Zone Manager Full in the zone, Accountant R, Trader No) and TRACE-02.0.E1: who traces, who exports, and what a search outside the zones finds.</summary>
public class LotTraceAccessTests
{
    [Fact]
    public async Task The_owner_and_the_zone_manager_trace_and_export_the_accountant_only_views_and_the_others_are_refused()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var accountant = await api.StaffAsync(null, StaffRole.Accountant);
        var zoneAccountant = await api.StaffAsync(await api.ZoneEntityAsync(sale.ZoneId), StaffRole.Accountant);
        var stranger = await api.ManagerAsync((await sale.Owner.Client.CreateZoneAsync()).Id());
        var trader = await api.TraderAsync();
        var code = (await sale.Owner.Client.TraceAsync(sale.LotId)).Text("lotCode");

        foreach (var client in new[] { sale.Owner.Client, sale.Manager.Client })
        {
            var trace = await client.TraceAsync(sale.LotId);
            Assert.Equal((true, true), (trace.GetProperty("canExport").GetBoolean(), trace.GetProperty("canCorrect").GetBoolean()));
            Assert.Equal(HttpStatusCode.OK, (await client.GetTraceExportAsync(sale.LotId)).StatusCode);
        }

        // R: the Accountant views the trace with the prices of the invoices it views, and neither exports, publishes nor corrects.
        foreach (var client in new[] { accountant.Client, zoneAccountant.Client })
        {
            var trace = await client.TraceAsync(sale.LotId);
            Assert.Equal((false, false, false), (trace.GetProperty("canExport").GetBoolean(), trace.GetProperty("canPublish").GetBoolean(), trace.GetProperty("canCorrect").GetBoolean()));
            Assert.Equal(ListPrice, trace.GetProperty("invoices")[0].GetProperty("lines")[0].GetProperty("unitPrice").GetDecimal());
            Assert.Equal(sale.LotId, (await client.FoundAsync(lotCode: code))[0]);
            var export = await client.GetTraceExportAsync(sale.LotId);
            Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (export.StatusCode, await export.ErrorCodeAsync()));
        }

        // Another zone: refused by id; a search finds nothing there (TRACE-02.0.E1, MSG-GEN-10). A trader traces nothing.
        foreach (var answer in new[] { await stranger.Client.GetTraceAsync(sale.LotId), await stranger.Client.GetTraceExportAsync(sale.LotId),
                     await trader.GetTraceAsync(sale.LotId), await trader.GetTraceExportAsync(sale.LotId), await trader.SearchTraceAsync(lotCode: code) })
        {
            Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (answer.StatusCode, await answer.ErrorCodeAsync()));
        }

        foreach (var search in new[] { await stranger.Client.SearchTraceAsync(lotCode: code), await stranger.Client.SearchTraceAsync(invoiceCode: invoice.Text("code")) })
        {
            var empty = await search.ReadAsync(HttpStatusCode.OK);
            Assert.Equal((0, "MSG-GEN-10"), (empty.GetProperty("lots").GetArrayLength(), empty.Text("messageCode")));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetTraceAsync(sale.LotId)).StatusCode);
    }

    [Fact]
    public async Task Unknown_lots_answer_404_and_a_search_needs_a_code()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await sale.Owner.Client.GetTraceAsync(Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sale.Owner.Client.GetTraceExportAsync(Guid.NewGuid())).StatusCode);
        var invalid = await (await sale.Manager.Client.SearchTraceAsync(lotCode: "  ")).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Lot code"), (invalid.Text("code"), invalid.Text("field")));
        var none = await (await sale.Manager.Client.SearchTraceAsync(invoiceCode: "HD999999")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("MSG-GEN-10", "There is no data for the sales invoice you chose. Please choose again."), (none.Text("messageCode"), none.Text("message")));
    }
}
