using System.Net;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Traceability;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesReturnTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>What the other modules see of a sales return (BR-ORIGIN-05, BR-TRACE-03, BR-TRACE-07, BR-CLAIM-02): the provenance page, the trace, the withdrawal audience and the claims.</summary>
public class SalesReturnTraceAndClaimTests
{
    private static object StillForSale(params Guid[] lots) => lots.Select(harvestLotId => new { harvestLotId, stillForSale = true }).ToArray();

    [Fact]
    public async Task A_lot_thrown_away_leaves_the_page_of_its_invoice_and_comes_back_when_the_return_is_cancelled()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var (invoice, token) = await sale.SoldWithQrCodeAsync((sale.LotId, 5m), (sale.OtherLotId, 5m));
        var fresh = await owner.InvoiceAsync(invoice.Id());
        var code = (await owner.GetHarvestLotAsync(sale.LotId)).Text("lotCode");
        Assert.Contains(code, (await owner.ProvenanceAsync(token)).LotsByCode().Keys);

        var saved = await owner.ReturnedAsync(invoice.Id(), ReturnBody(fresh, "ThrownAway", "BackForSale").With("thrownAwayLots", StillForSale(sale.LotId)));

        var page = await owner.ProvenanceAsync(token);
        Assert.Equal("Ok", page.Text("status"));
        Assert.DoesNotContain(code, page.LotsByCode().Keys);
        Assert.Single(page.LotsByCode());

        await (await owner.CancelSalesReturnAsync(saved.GetProperty("return").Id(), CancelBody(saved.GetProperty("return")))).ReadAsync(HttpStatusCode.OK);
        Assert.Contains(code, (await owner.ProvenanceAsync(token)).LotsByCode().Keys);
    }

    [Fact]
    public async Task The_trace_lists_the_return_and_a_returned_invoice_is_left_out_of_a_withdrawal()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        var invoice = await owner.InvoiceAsync(sold.Id());
        var saved = await owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "ThrownAway").With("thrownAwayLots", StillForSale(sale.LotId)));
        await api.PublishLotAsync(sale.LotId);

        var trace = await (await owner.GetAsync($"/api/harvest-lots/{sale.LotId}/trace")).ReadAsync(HttpStatusCode.OK);
        var returned = trace.GetProperty("invoices")[0].GetProperty("returns");
        Assert.Equal(1, returned.GetArrayLength());
        Assert.Equal((saved.GetProperty("return").Text("code"), 10m, "ThrownAway"), (returned[0].Text("code"), returned[0].Kg("kg"), returned[0].Text("outcome")));

        var preview = await (await owner.GetAsync($"/api/harvest-lots/{sale.LotId}/withdraw-preview")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(0, preview.GetProperty("traders").GetArrayLength());
    }

    [Fact]
    public async Task A_claim_cannot_be_accepted_once_the_invoice_is_returned_and_can_again_after_the_return_is_cancelled()
    {
        using var api = new AuthApiFactory();
        var world = await api.ClaimWorldAsync();
        var owner = world.Sale.Owner.Client;
        var claim = await world.Trader.Client.SubmitClaimAsync(world.InvoiceId);
        var invoice = await owner.InvoiceAsync(world.InvoiceId);
        var saved = await owner.ReturnedAsync(world.InvoiceId, ReturnBody(invoice, "BackForSale"));

        var staffView = await owner.ClaimAsStaffAsync(claim.Id());
        await (await owner.AcceptClaimAsync(claim.Id(), staffView.Text("rowVersion"))).ProblemAsync(HttpStatusCode.Conflict, "MSG-CLAIM-03");
        var preview = await (await owner.PreviewReturnAsync(world.InvoiceId)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Contains("returned", preview.Text("detail"));

        await (await owner.CancelSalesReturnAsync(saved.GetProperty("return").Id(), CancelBody(saved.GetProperty("return")))).ReadAsync(HttpStatusCode.OK);
        await (await owner.AcceptClaimAsync(claim.Id(), staffView.Text("rowVersion"))).ReadAsync(HttpStatusCode.OK);
    }
}
