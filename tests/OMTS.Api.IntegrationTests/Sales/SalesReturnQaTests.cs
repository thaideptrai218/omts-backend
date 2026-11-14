using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Traceability;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesReturnTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of CLAIM-03 (OMTS-225): what the author's tests leave out of the view, the money after a cancellation, the lots and who may read a return.</summary>
public class SalesReturnQaTests
{
    private static async Task<(AuthApiFactory Api, SaleSetup Sale)> StartAsync()
    {
        var api = new AuthApiFactory();
        return (api, await api.SaleSetupAsync());
    }

    private static async Task<JsonElement> TwoLinesOfOneLotAsync(SaleSetup sale)
    {
        var sold = await sale.Owner.Client.SellAsync(Invoice(
            sale.CustomerId, [Line(sale.ProductId, sale.LotId, 5m), Line(sale.ProductId, sale.LotId, 5m, 40_000m, priceDecidedByOwner: true)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.LotId));
        return await sale.Owner.Client.InvoiceAsync(sold.GetProperty("invoice").Id());
    }

    // CLAIM-03.3 step 2 shows "the remaining kg of each harvest lot whose oysters went back for sale". The read is no-tracking, so the two lines of one lot hold two HarvestLot objects.
    [Fact]
    public async Task Qa_the_return_shows_one_row_per_lot_and_takes_all_its_kg_out_after_a_cancellation()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        var invoice = await TwoLinesOfOneLotAsync(sale);
        var remainingWhileSold = (await owner.GetHarvestLotAsync(sale.LotId)).GetProperty("figures").Kg("remainingKg");

        var saved = await owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"));

        var read = await (await owner.GetSalesReturnAsync(saved.GetProperty("return").Id())).ReadAsync(HttpStatusCode.OK);
        var lot = Assert.Single(read.GetProperty("lots").EnumerateArray());
        Assert.Equal(remainingWhileSold + 10m, lot.Kg("remainingKg"));
        Assert.Equal(remainingWhileSold, lot.Kg("remainingAfterCancelKg"));
    }

    // CLAIM-03.3 step 4: "shows that it is saved (MSG-GEN-05)", as CLAIM-02 was fixed to do in its QA.
    [Fact]
    public async Task Qa_cancelling_a_return_answers_msg_gen_05()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        var invoice = await owner.InvoiceAsync((await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice)).Id());
        var returned = (await owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"))).GetProperty("return");

        var answer = await (await owner.CancelSalesReturnAsync(returned.Id(), CancelBody(returned))).ReadAsync(HttpStatusCode.OK);

        Assert.True(answer.TryGetProperty("messageCode", out var code), "The cancellation answers no message.");
        Assert.Equal("MSG-GEN-05", code.GetString());
    }

    [Fact]
    public async Task Qa_a_return_is_read_in_its_zone_only_and_only_the_owner_cancels_it()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        var invoice = await owner.InvoiceAsync((await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice)).Id());
        var returned = (await owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"))).GetProperty("return");
        var outsider = await api.ManagerAsync((await owner.CreateZoneAsync()).Id());
        var accountant = await api.AllZonesAccountantAsync();

        await (await sale.Manager.Client.GetSalesReturnAsync(returned.Id())).ReadAsync(HttpStatusCode.OK);
        await (await outsider.Client.GetSalesReturnAsync(returned.Id())).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await accountant.Client.GetSalesReturnAsync(returned.Id())).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await accountant.Client.CancelSalesReturnAsync(returned.Id(), CancelBody(returned))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetSalesReturnAsync(Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetSalesReturnAsync(returned.Id())).StatusCode);
    }

    // BR-DEBT-02: a return cancelled after the money was paid back leaves the customer owing that money; BR-DEBT-05 (5): the invoice is unpaid for it.
    [Fact]
    public async Task Qa_cancelling_a_return_after_the_money_was_paid_back_puts_the_invoice_on_the_debt()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        var accountant = await api.AllZonesAccountantAsync();
        var paid = (await owner.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m), sale.LotId))).GetProperty("invoice");
        var invoice = await owner.InvoiceAsync(paid.Id());
        var returned = (await owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"))).GetProperty("return");
        await (await accountant.Client.PayBackAsync(MoneyBody(sale.CustomerId, 300_000m))).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(0m, await owner.CustomerDebtAsync(sale.CustomerId));

        await (await owner.CancelSalesReturnAsync(returned.Id(), CancelBody(returned))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(300_000m, await owner.CustomerDebtAsync(sale.CustomerId));
        var effect = (await owner.InvoiceAsync(invoice.Id())).GetProperty("debtEffect");
        Assert.Equal((false, 0m, 300_000m), (effect.GetProperty("isPaid").GetBoolean(), effect.Money("covered"), effect.Money("unpaid")));
        var statement = await (await owner.GetAsync($"/api/customer-debts/{sale.CustomerId}/statement")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(300_000m, statement.Money("closingDebt"));
        Assert.Equal(300_000m, Assert.Single(statement.GetProperty("unpaidInvoices").EnumerateArray()).Money("unpaid"));
    }

    // BR-DEBT-05 (3): the cash that came with the invoice taken back covers the oldest invoices.
    [Fact]
    public async Task Qa_the_cash_of_a_returned_invoice_covers_the_older_invoice_and_the_unpaid_add_up_to_the_debt()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        var older = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        var paid = (await owner.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.OtherLotId, 5m)], Cash(150_000m), sale.OtherLotId))).GetProperty("invoice");
        var invoice = await owner.InvoiceAsync(paid.Id());
        Assert.Equal(300_000m, await owner.CustomerDebtAsync(sale.CustomerId));

        var saved = await owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"));

        Assert.Equal(150_000m, saved.Money("debtAfter"));
        var statement = await (await owner.GetAsync($"/api/customer-debts/{sale.CustomerId}/statement")).ReadAsync(HttpStatusCode.OK);
        var unpaid = Assert.Single(statement.GetProperty("unpaidInvoices").EnumerateArray());
        Assert.Equal((older.Id(), 300_000m, 150_000m, 150_000m), (unpaid.Id(), unpaid.Money("total"), unpaid.Money("covered"), unpaid.Money("unpaid")));
        Assert.Equal(150_000m, statement.Money("debtToday"));
        Assert.True((await owner.InvoiceAsync(invoice.Id())).GetProperty("debtEffect").GetProperty("isPaid").GetBoolean());
    }

    // BR-CLAIM-03: a walk-in invoice keeps the profile after the cancellation: its own payments net out with the invoice.
    [Fact]
    public async Task Qa_a_cancelled_return_of_a_walk_in_invoice_leaves_it_on_the_profile_with_no_debt()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        var sold = await owner.SellAsync(Invoice(null, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m), sale.LotId));
        var invoice = await owner.InvoiceAsync(sold.GetProperty("invoice").Id());
        var saved = await owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale").With("customerId", sale.CustomerId));
        var returned = saved.GetProperty("return");

        await (await owner.CancelSalesReturnAsync(returned.Id(), CancelBody(returned))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(0m, await owner.CustomerDebtAsync(sale.CustomerId));
        var after = await owner.InvoiceAsync(invoice.Id());
        Assert.Equal(sale.CustomerId, after.GetProperty("customer").Id());
        Assert.True(after.GetProperty("debtEffect").GetProperty("isPaid").GetBoolean());
    }

    // CLAIM-01: no claim on a returned invoice; after the cancellation it is a Completed invoice again.
    [Fact]
    public async Task Qa_a_claim_is_refused_on_a_returned_invoice_and_accepted_once_the_return_is_cancelled()
    {
        using var api = new AuthApiFactory();
        var world = await api.ClaimWorldAsync();
        var owner = world.Sale.Owner.Client;
        var invoice = await owner.InvoiceAsync(world.InvoiceId);
        var returned = (await owner.ReturnedAsync(world.InvoiceId, ReturnBody(invoice, "BackForSale"))).GetProperty("return");

        var refused = await (await world.Trader.Client.PostClaimAsync(world.InvoiceId)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Contains("returned", refused.Text("detail"));

        await (await owner.CancelSalesReturnAsync(returned.Id(), CancelBody(returned))).ReadAsync(HttpStatusCode.OK);
        await world.Trader.Client.SubmitClaimAsync(world.InvoiceId);
    }

    // BR-HARVEST-04: the earlier close stays in the audit log only, and the days before automatic close count from the reopening.
    [Fact]
    public async Task Qa_a_lot_closed_with_a_leftover_and_taken_back_loses_the_leftover_and_restarts_its_days()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        var invoice = await owner.InvoiceAsync((await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice)).Id());
        await api.CloseLotAsync(owner, sale.LotId, 5m, "Unsold");

        await owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"));

        var lot = await HarvestLotCloseTestKit.LotRowAsync(api, sale.LotId);
        Assert.Equal(StatusIds.HarvestLot.Reopened, lot.StatusId);
        Assert.Null(lot.LeftoverKg);
        Assert.Null(lot.CloseReason);
        Assert.Equal(FarmingTestKit.Today, lot.ReopenedOn);
        var figures = (await owner.GetHarvestLotAsync(sale.LotId)).GetProperty("figures");
        Assert.Equal((0m, 10m), (figures.Kg("lostKg"), figures.Kg("returnedKg")));
    }

    // BR-CLAIM-03: after the cancellation the discarded kg no longer counts in the lost kg and the lot is Closed still.
    [Fact]
    public async Task Qa_the_cancellation_of_a_return_thrown_away_takes_the_discarded_kg_out_of_the_lost_kg_and_keeps_the_lot_closed()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        var invoice = await owner.InvoiceAsync((await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice)).Id());
        var saved = await sale.Manager.Client.ReturnedAsync(
            invoice.Id(), ReturnBody(invoice, "ThrownAway").With("ownerAgreed", true).With("thrownAwayLots", new[] { new { harvestLotId = sale.LotId, stillForSale = false, leftoverKg = 0m } }));
        var returned = saved.GetProperty("return");
        Assert.Equal(10m, (await owner.GetHarvestLotAsync(sale.LotId)).GetProperty("figures").Kg("lostKg"));
        Assert.Equal(StatusIds.HarvestLot.Closed, (await HarvestLotCloseTestKit.LotRowAsync(api, sale.LotId)).StatusId);

        await (await owner.CancelSalesReturnAsync(returned.Id(), CancelBody(returned))).ReadAsync(HttpStatusCode.OK);

        var figures = (await owner.GetHarvestLotAsync(sale.LotId)).GetProperty("figures");
        Assert.Equal((0m, 0m, 10m), (figures.Kg("lostKg"), figures.Kg("returnedKg"), figures.Kg("soldKg")));
        Assert.Equal(StatusIds.HarvestLot.Closed, (await HarvestLotCloseTestKit.LotRowAsync(api, sale.LotId)).StatusId);
    }

    // BR-CLAIM-03 "a cancellation sends no notification"; a question or refusal spends no number.
    [Fact]
    public async Task Qa_a_refused_request_spends_no_number_and_a_cancellation_notifies_nobody()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        await api.AllZonesAccountantAsync();
        var invoice = await owner.InvoiceAsync((await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice)).Id());
        await (await sale.Manager.Client.PostReturnAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"))).QuestionAsync();
        await (await owner.PostReturnAsync(invoice.Id(), ReturnBody(invoice, "ThrownAway"))).QuestionAsync();

        var returned = (await owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"))).GetProperty("return");

        Assert.EndsWith("000001", returned.Text("code"));
        var notices = () => api.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == NotificationTypes.SalesReturnRecorded || n.Type == NotificationTypes.CustomerMoneyOwedBack));
        var before = await notices();
        await (await owner.CancelSalesReturnAsync(returned.Id(), CancelBody(returned))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(before, await notices());
    }

    // BR-TRACE-07: a cancelled return puts the trader back in the audience of a withdrawal.
    [Fact]
    public async Task Qa_a_cancelled_return_puts_the_trader_back_in_the_withdrawal_audience()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        var invoice = await owner.InvoiceAsync((await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice)).Id());
        var returned = (await owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "ThrownAway").With("thrownAwayLots", new[] { new { harvestLotId = sale.LotId, stillForSale = true } }))).GetProperty("return");
        await api.PublishLotAsync(sale.LotId);
        var path = $"/api/harvest-lots/{sale.LotId}/withdraw-preview";
        Assert.Equal(0, (await (await owner.GetAsync(path)).ReadAsync(HttpStatusCode.OK)).GetProperty("traders").GetArrayLength());

        await (await owner.CancelSalesReturnAsync(returned.Id(), CancelBody(returned))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(1, (await (await owner.GetAsync(path)).ReadAsync(HttpStatusCode.OK)).GetProperty("traders").GetArrayLength());
    }
}
