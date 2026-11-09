using System.Net;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Returns;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesReturnTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>CLAIM-03: who records a return, what it does to the invoice, the debt and the lots (AC-09), and the Owner's cancellation.</summary>
public class SalesReturnTests
{
    private static async Task<(AuthApiFactory Api, SaleSetup Sale)> StartAsync()
    {
        var api = new AuthApiFactory();
        return (api, await api.SaleSetupAsync());
    }

    private static async Task<System.Text.Json.JsonElement> OnDebtAsync(SaleSetup sale, Guid lotId, decimal kg, bool closeLot = false) =>
        await sale.Owner.Client.InvoiceAsync((await sale.SoldOnDebtAsync(lotId, kg, ListPrice, notSoldWhole: !closeLot)).Id());

    [Fact]
    public async Task AC_09_the_return_reopens_the_lot_and_each_kg_counts_once()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var lot = await owner.HarvestLotAsync(sale.RaftId, new HarvestFields { Lanes = ["3"], Cages = "6" });
        var invoiceA = await OnDebtAsync(sale, lot.Id(), 300m, closeLot: true);
        Assert.Equal("Closed", (await owner.GetHarvestLotAsync(lot.Id())).Text("status"));
        Assert.Equal(9_000_000m, await owner.CustomerDebtAsync(sale.CustomerId));

        var saved = await owner.ReturnedAsync(invoiceA.Id(), ReturnBody(invoiceA, "BackForSale"));

        Assert.Equal((0m, 0m, 9_000_000m), (saved.Money("debtAfter"), saved.Money("owedBack"), saved.GetProperty("return").Money("value")));
        Assert.Equal("Reopened", (await owner.GetHarvestLotAsync(lot.Id())).Text("status"));
        Assert.Equal(0m, await owner.CustomerDebtAsync(sale.CustomerId));
        var locked = await owner.InvoiceAsync(invoiceA.Id());
        Assert.Equal(["CancelReturn", "Print"], locked.Actions());
        await (await owner.CorrectAsync(invoiceA.Id(), PriceCut(locked, 100m))).ProblemAsync(HttpStatusCode.Conflict, "MSG-SALE-20");
        await (await owner.CancelInvoiceAsync(invoiceA.Id(), Cancellation(locked))).ProblemAsync(HttpStatusCode.Conflict, "MSG-SALE-20");

        await sale.SoldOnDebtAsync(lot.Id(), 300m, ListPrice, notSoldWhole: false);

        var figures = (await owner.YieldAsync(HarvestYieldTestKit.YieldFilters(FarmingTestKit.Today, FarmingTestKit.Today, zoneIds: sale.ZoneId))).YieldLot(lot.Id());
        Assert.Equal((600m, 300m, 300m), (figures.Kg("invoicedKg"), figures.Kg("returnedKg"), figures.Kg("soldKg")));
        Assert.Equal(0m, (await owner.GetHarvestLotAsync(lot.Id())).GetProperty("figures").Kg("remainingKg"));
    }

    [Fact]
    public async Task A_zone_manager_confirms_the_owner_agreed_and_the_owner_is_not_asked()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var invoice = await OnDebtAsync(sale, sale.LotId, 10m);
        var manager = sale.Manager.Client;

        var question = await (await manager.PostReturnAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"))).QuestionAsync();
        Assert.Equal(("MSG-CLAIM-04", "ownerAgreed"), (question.Code, question.Field));
        Assert.Empty(await api.ReturnsOfAsync(invoice.Id()));

        var saved = await manager.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale").With("ownerAgreed", true));

        Assert.Equal("TL", saved.GetProperty("return").Text("code")![..2]);
        Assert.Equal(sale.Manager.Account.Id, saved.GetProperty("return").GetProperty("recordedBy").Id());
        Assert.Single(await api.InvoiceAuditAsync(saved.GetProperty("return").Id(), SalesReturnService.RecordedAction));
    }

    [Fact]
    public async Task Whatever_is_refused_saves_nothing_and_the_rights_follow_the_zone()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        var invoice = await OnDebtAsync(sale, sale.LotId, 10m);
        var accountant = await api.AllZonesAccountantAsync();
        var otherZone = await owner.CreateZoneAsync();
        var outsider = await api.ManagerAsync(otherZone.Id());

        await (await owner.PostReturnAsync(invoice.Id(), ReturnBody(invoice, "BackForSale").With("reason", " "))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        await (await owner.PostReturnAsync(invoice.Id(), ReturnBody(invoice, "BackForSale").With("lines", new[] { new { invoiceLineId = Guid.NewGuid(), outcome = "BackForSale" } })))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        await (await owner.PostReturnAsync(invoice.Id(), ReturnBody(invoice, "BackForSale").With("rowVersion", null))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        await (await owner.PostReturnAsync(invoice.Id(), ReturnBody(invoice, "BackForSale").With("customerId", sale.CustomerId))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        await (await accountant.Client.PostReturnAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await outsider.Client.PostReturnAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await outsider.Client.PreviewReturnAsync(invoice.Id())).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().PreviewReturnAsync(invoice.Id())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostReturnAsync(Guid.NewGuid(), ReturnBody(invoice, "BackForSale"))).StatusCode);

        // Offers match refusals: a manager of another zone is not offered the return.
        Assert.DoesNotContain("CreateReturn", (await accountant.Client.InvoiceAsync(invoice.Id())).Actions());
        Assert.Contains("CreateReturn", (await owner.InvoiceAsync(invoice.Id())).Actions());

        await owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"));
        var locked = await owner.InvoiceAsync(invoice.Id());
        await (await owner.PostReturnAsync(invoice.Id(), ReturnBody(locked, "BackForSale"))).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.DoesNotContain("CreateReturn", locked.Actions());
        Assert.Single(await api.ReturnsOfAsync(invoice.Id()));

        var cancelled = await sale.SoldOnDebtAsync(sale.OtherLotId, 5m, ListPrice);
        await owner.CancelledAsync(cancelled.Id(), Cancellation(await owner.InvoiceAsync(cancelled.Id())));
        var gone = await owner.InvoiceAsync(cancelled.Id());
        Assert.DoesNotContain("CreateReturn", gone.Actions());
        await (await owner.PostReturnAsync(cancelled.Id(), ReturnBody(gone, "BackForSale"))).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }

    [Fact]
    public async Task The_return_of_a_paid_invoice_is_money_owed_back_for_the_accountant_to_pay()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        var accountant = await api.AllZonesAccountantAsync();
        var paid = (await owner.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m), sale.LotId))).GetProperty("invoice");
        var invoice = await owner.InvoiceAsync(paid.Id());

        var saved = await owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"));

        Assert.Equal((-300_000m, 300_000m), (saved.Money("debtAfter"), saved.Money("owedBack")));
        Assert.Equal(-300_000m, await owner.CustomerDebtAsync(sale.CustomerId));
        var notices = await api.ReturnNoticesAsync(invoice.Id());
        var toOwner = Assert.Single(notices, n => n.Type == NotificationTypes.SalesReturnRecorded);
        Assert.Equal(sale.Owner.Account.Id, toOwner.RecipientAccountId);
        Assert.Equal("MSG-CLAIM-05", toOwner.MessageCode);
        var toAccountant = Assert.Single(notices, n => n.Type == NotificationTypes.CustomerMoneyOwedBack);
        Assert.Equal(accountant.Account.Id, toAccountant.RecipientAccountId);
        Assert.Equal("MSG-CLAIM-05", toAccountant.MessageCode);
        Assert.Contains("Please pay the money back", toAccountant.Body);

        // The invoice is covered by its return (BR-DEBT-05): paid, and the money goes back in DEBT-02.
        Assert.True((await owner.InvoiceAsync(invoice.Id())).GetProperty("debtEffect").GetProperty("isPaid").GetBoolean());
        await (await accountant.Client.PayBackAsync(MoneyBody(sale.CustomerId, 300_000m))).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(0m, await owner.CustomerDebtAsync(sale.CustomerId));
    }

    [Fact]
    public async Task A_return_on_the_debt_tells_only_the_owner_and_the_statement_shows_it()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        await api.AllZonesAccountantAsync();
        var invoice = await OnDebtAsync(sale, sale.LotId, 10m);

        var saved = await owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"));

        Assert.Equal(0m, saved.Money("debtAfter"));
        var notice = Assert.Single(await api.ReturnNoticesAsync(invoice.Id()));
        Assert.Equal(NotificationTypes.SalesReturnRecorded, notice.Type);
        Assert.Contains("now owes the cooperative 0 VND", notice.Body);
        var code = saved.GetProperty("return").Text("code");
        var statement = await (await owner.GetAsync($"/api/customer-debts/{sale.CustomerId}/statement")).ReadAsync(HttpStatusCode.OK);
        var entry = statement.GetProperty("entries").EnumerateArray().Single(e => e.Text("kind") == "Return");
        Assert.Equal((code, 300_000m, -300_000m, false), (entry.Text("code"), entry.Money("amount"), entry.Money("debtChange"), entry.GetProperty("cancelled").GetBoolean()));
        Assert.Equal(0m, statement.Money("closingDebt"));

        await owner.CancelSalesReturnAsync(saved.GetProperty("return").Id(), CancelBody(saved.GetProperty("return")));
        var after = await (await owner.GetAsync($"/api/customer-debts/{sale.CustomerId}/statement")).ReadAsync(HttpStatusCode.OK);
        var cancelled = after.GetProperty("entries").EnumerateArray().Single(e => e.Text("kind") == "Return");
        Assert.True(cancelled.GetProperty("cancelled").GetBoolean());
        Assert.Equal(0m, cancelled.Money("debtChange"));
        Assert.Equal(300_000m, after.Money("closingDebt"));
    }

    [Fact]
    public async Task A_walk_in_invoice_gets_the_profile_of_the_trader_with_its_payments()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        var sold = await owner.SellAsync(Invoice(null, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m), sale.LotId));
        var invoice = await owner.InvoiceAsync(sold.GetProperty("invoice").Id());
        Assert.Equal(0m, await owner.CustomerDebtAsync(sale.CustomerId));

        await (await owner.PostReturnAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        var preview = await (await owner.PreviewReturnAsync(invoice.Id(), sale.CustomerId)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((true, 0m, -300_000m), (preview.GetProperty("requiresCustomer").GetBoolean(), preview.Money("customerDebt"), preview.Money("debtAfter")));

        var saved = await owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale").With("customerId", sale.CustomerId));

        Assert.Equal(-300_000m, saved.Money("debtAfter"));
        Assert.Equal(-300_000m, await owner.CustomerDebtAsync(sale.CustomerId));
        var after = await owner.InvoiceAsync(invoice.Id());
        Assert.Equal(sale.CustomerId, after.GetProperty("customer").Id());
        Assert.All(await api.PaymentsOfAsync(invoice.Id()), p => Assert.Equal(sale.CustomerId, p.CustomerId));
    }

    [Fact]
    public async Task Cancelling_restores_the_rights_and_the_money_and_keeps_the_status_of_the_lot()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        var invoice = await OnDebtAsync(sale, sale.LotId, 10m, closeLot: true);
        var saved = await owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"));
        var returned = saved.GetProperty("return");
        var manager = sale.Manager.Client;

        await (await manager.CancelSalesReturnAsync(returned.Id(), CancelBody(returned))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await owner.CancelSalesReturnAsync(returned.Id(), CancelBody(returned, reason: " "))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");

        var cancelled = await (await owner.CancelSalesReturnAsync(returned.Id(), CancelBody(returned))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal("Cancelled", cancelled.Text("status"));
        Assert.Equal(300_000m, await owner.CustomerDebtAsync(sale.CustomerId));
        Assert.Equal("Reopened", (await owner.GetHarvestLotAsync(sale.LotId)).Text("status"));
        var figures = (await owner.YieldAsync(HarvestYieldTestKit.YieldFilters(FarmingTestKit.Today, FarmingTestKit.Today))).YieldLot(sale.LotId);
        Assert.Equal((10m, 0m), (figures.Kg("invoicedKg"), figures.Kg("returnedKg")));
        var restored = await owner.InvoiceAsync(invoice.Id());
        Assert.Contains("Correct", restored.Actions());
        Assert.Contains("Cancel", restored.Actions());
        Assert.Contains("CreateReturn", restored.Actions());
        Assert.DoesNotContain("CancelReturn", restored.Actions());
        await (await owner.CancelSalesReturnAsync(returned.Id(), CancelBody(cancelled))).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        // The rights are back, also for a new return of the same invoice.
        await owner.CorrectedAsync(invoice.Id(), PriceCut(restored, 100_000m));
        var corrected = await owner.InvoiceAsync(invoice.Id());
        var again = await owner.ReturnedAsync(invoice.Id(), ReturnBody(corrected, "BackForSale"));
        Assert.Equal(200_000m, again.GetProperty("return").Money("value"));
        Assert.Equal(2, (await api.ReturnsOfAsync(invoice.Id())).Count);
        Assert.Equal(0m, await owner.CustomerDebtAsync(sale.CustomerId));
    }

    [Fact]
    public async Task Cancelling_warns_when_the_oysters_were_sold_since_and_never_touches_the_status_of_the_lot()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var lot = await owner.HarvestLotAsync(sale.RaftId, new HarvestFields { Lanes = ["4"], Cages = "2" });
        var first = await OnDebtAsync(sale, lot.Id(), 100m, closeLot: true);
        var saved = await owner.ReturnedAsync(first.Id(), ReturnBody(first, "BackForSale"));
        var returned = saved.GetProperty("return");
        // The kg back for sale is sold again, whole, so the lot is Closed by the new invoice.
        await sale.SoldOnDebtAsync(lot.Id(), 100m, ListPrice, notSoldWhole: false);
        Assert.Equal("Closed", (await owner.GetHarvestLotAsync(lot.Id())).Text("status"));

        var warning = await (await owner.CancelSalesReturnAsync(returned.Id(), CancelBody(returned))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-CLAIM-06", "confirmNegativeRemaining"), (warning.Text("code"), warning.Text("confirmationField")));
        Assert.Contains("(-100 kg)", warning.Text("detail"));

        await (await owner.CancelSalesReturnAsync(returned.Id(), CancelBody(returned).With("confirmNegativeRemaining", true))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Closed", (await owner.GetHarvestLotAsync(lot.Id())).Text("status"));
    }

    [Fact]
    public async Task Oysters_thrown_away_ask_if_the_lot_stays_for_sale_and_count_as_lost()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        var keep = await OnDebtAsync(sale, sale.LotId, 10m);

        var question = await (await owner.PostReturnAsync(keep.Id(), ReturnBody(keep, "ThrownAway"))).QuestionAsync();
        Assert.Equal(("MSG-HARVEST-02", "thrownAwayLots"), (question.Code, question.Field));
        Assert.Equal(10m, question.Body.Kg("discardedKg"));

        var kept = await owner.ReturnedAsync(keep.Id(), ReturnBody(keep, "ThrownAway").With("thrownAwayLots", new[] { new { harvestLotId = sale.LotId, stillForSale = true } }));
        Assert.Equal(10m, kept.GetProperty("return").GetProperty("lines")[0].Kg("discardedKg"));
        var lot = await owner.GetHarvestLotAsync(sale.LotId);
        Assert.Equal("Open", lot.Text("status"));
        var figures = (await owner.YieldAsync(HarvestYieldTestKit.YieldFilters(FarmingTestKit.Today, FarmingTestKit.Today))).YieldLot(sale.LotId);
        Assert.Equal((10m, 10m, 0m, 10m), (figures.Kg("invoicedKg"), figures.Kg("returnedKg"), figures.Kg("soldKg"), figures.Kg("lostKg")));

        // No closes the lot through HARVEST-02.1, with the leftover kg of what is still unsold.
        var close = await OnDebtAsync(sale, sale.OtherLotId, 10m);
        var closed = await owner.ReturnedAsync(close.Id(), ReturnBody(close, "ThrownAway").With("thrownAwayLots", new[]
        {
            new { harvestLotId = sale.OtherLotId, stillForSale = false, leftoverKg = 5m, closeReason = "Spoiled" },
        }));
        Assert.NotEmpty(closed.GetProperty("return").GetProperty("lines").EnumerateArray());
        Assert.Equal(("Closed", 5m), await ClosedLotAsync(api, sale.OtherLotId));
        // A lot that was already Closed is not asked about.
        var whole = await OnDebtAsync(sale, (await owner.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(5))).Id(), 10m, closeLot: true);
        await owner.ReturnedAsync(whole.Id(), ReturnBody(whole, "ThrownAway"));
    }

    private static async Task<(string Status, decimal? Leftover)> ClosedLotAsync(AuthApiFactory api, Guid lotId)
    {
        var lot = await api.LotOfAsync(lotId);
        return (lot.StatusId == StatusIds.HarvestLot.Closed ? "Closed" : "Open", lot.LeftoverKg);
    }

    [Fact]
    public async Task A_lot_taken_back_for_sale_on_the_same_return_is_not_asked_about()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        var sold = await owner.SellAsync(Invoice(
            sale.CustomerId, [Line(sale.ProductId, sale.LotId, 5m), Line(sale.ProductId, sale.LotId, 5m, 40_000m, priceDecidedByOwner: true)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.LotId));
        var invoice = await owner.InvoiceAsync(sold.GetProperty("invoice").Id());

        var saved = await owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale", "ThrownAway"));

        Assert.Equal(new[] { "BackForSale", "ThrownAway" }, saved.GetProperty("return").GetProperty("lines").EnumerateArray().Select(l => l.Text("outcome")));
        var lines = (await api.ReturnsOfAsync(invoice.Id())).Single().Lines.ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(5m, lines.Sum(l => l.DiscardedKg));
        await (await owner.PostReturnAsync(invoice.Id(), ReturnBody(invoice, "ThrownAway"))).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }

    [Fact]
    public async Task A_push_that_fails_undoes_nothing()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        var invoice = await OnDebtAsync(sale, sale.LotId, 10m);
        api.RefusePushTo(sale.Owner.Account.Id);

        await owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"));

        Assert.Single(await api.ReturnsOfAsync(invoice.Id()));
        Assert.Single(await api.ReturnNoticesAsync(invoice.Id()));
    }

    // The return keeps its record of a line by id, with no foreign key: after a cancelled return, a correction may remove the line.
    [Fact]
    public async Task A_correction_after_a_cancelled_return_may_remove_a_line_of_that_return()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        var sold = await owner.SellAsync(Invoice(
            sale.CustomerId, [Line(sale.ProductId, sale.LotId, 5m), Line(sale.ProductId, sale.OtherLotId, 5m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.LotId, sale.OtherLotId));
        var invoice = await owner.InvoiceAsync(sold.GetProperty("invoice").Id());
        var saved = await owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"));
        await (await owner.CancelSalesReturnAsync(saved.GetProperty("return").Id(), CancelBody(saved.GetProperty("return")))).ReadAsync(HttpStatusCode.OK);
        var restored = await owner.InvoiceAsync(invoice.Id());

        var corrected = await owner.CorrectedAsync(invoice.Id(), DataEntry(restored, [Line(sale.ProductId, sale.LotId, 5m)], sale.LotId));

        Assert.Equal(1, corrected.GetProperty("invoice").GetProperty("lines").GetArrayLength());
        var cancelled = await (await owner.GetSalesReturnAsync(saved.GetProperty("return").Id())).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(2, cancelled.GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task The_preview_shows_the_lines_the_value_and_the_claims()
    {
        var (api, sale) = await StartAsync();
        using var _ = api;
        var owner = sale.Owner.Client;
        var invoice = await OnDebtAsync(sale, sale.LotId, 10m);

        var preview = await (await sale.Manager.Client.PreviewReturnAsync(invoice.Id())).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((300_000m, 300_000m, 0m, false), (preview.Money("value"), preview.Money("customerDebt"), preview.Money("debtAfter"), preview.GetProperty("requiresCustomer").GetBoolean()));
        Assert.True(preview.GetProperty("ownerMustAgree").GetBoolean());
        Assert.False((await (await owner.PreviewReturnAsync(invoice.Id())).ReadAsync(HttpStatusCode.OK)).GetProperty("ownerMustAgree").GetBoolean());
        Assert.Equal(invoice.Text("rowVersion"), preview.Text("rowVersion"));
    }
}
