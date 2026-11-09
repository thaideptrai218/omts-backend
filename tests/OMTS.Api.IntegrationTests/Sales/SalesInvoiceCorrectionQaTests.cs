using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>Independent QA of SALE-07 (BR-SALE-10, BR-DEBT-02, BR-DEBT-05, AC-08): the money everywhere it is shown, repeated corrections, the walk-in rules, the profile change, the marks and the inputs.</summary>
public class SalesInvoiceCorrectionQaTests
{
    private static async Task<Guid> NewCustomerAsync(SaleSetup sale) => (await sale.Owner.Client.CreateCustomerAsync()).Id();

    private static async Task<JsonElement> DetailAsync(SaleSetup sale, JsonElement invoice) => await sale.Owner.Client.InvoiceAsync(invoice.Id());

    // AC-08 end to end: one figure on every screen, the debt read twice does not move, and the paid state follows the latest total.
    [Fact]
    public async Task Ac_08_moves_the_debt_once_and_every_screen_shows_the_same_figure()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 200m, 50_000m);
        await owner.FinishedAsync(sold.Id(), wantsQrCode: false);
        var before = await owner.InvoiceAsync(sold.Id());

        var invoice = (await owner.CorrectedAsync(sold.Id(), PriceCut(before, 500_000m))).GetProperty("invoice");

        Assert.Equal(9_500_000m, invoice.Money("total"));
        Assert.Equal((9_500_000m, 9_500_000m, 9_500_000m), await owner.DebtsEverywhereAsync(sale.CustomerId));
        Assert.Equal(9_500_000m, await owner.CustomerDebtAsync(sale.CustomerId));
        var effect = (await owner.InvoiceAsync(sold.Id())).GetProperty("debtEffect");
        Assert.Equal((false, 9_500_000m), (effect.GetProperty("isPaid").GetBoolean(), effect.Money("unpaid")));
        var list = await owner.InvoiceListAsync();
        Assert.Equal(9_500_000m, list.Money("completedTotal"));
        Assert.Equal(9_500_000m, list.Rows().Single(r => r.Id() == sold.Id()).Money("total"));
        var statement = await owner.StatementAsync(sale.CustomerId);
        Assert.Equal(9_500_000m, statement.GetProperty("unpaidInvoices").EnumerateArray().Single().Money("unpaid"));
        var lineEntry = statement.GetProperty("entries").EnumerateArray().Single(e => e.Id() == sold.Id());
        Assert.Equal(9_500_000m, lineEntry.Money("debtChange"));
    }

    // BR-DEBT-05 (3): the part of the cash that the invoice no longer needs after a price cut covers the oldest invoice; the unpaid amounts add up to the debt.
    [Fact]
    public async Task After_a_price_cut_the_cash_the_invoice_no_longer_needs_covers_the_oldest_invoice()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var customer = await NewCustomerAsync(sale);
        var older = await sale.PayLaterAsync(customer, 10m);
        await api.BackDateAsync(older.Id(), 3);
        var newer = await sale.PayLaterAsync(customer, 10m, cash: 100_000m);

        await owner.CorrectedAsync(newer.Id(), PriceCut(await DetailAsync(sale, newer), 250_000m));

        Assert.Equal(250_000m, await owner.CustomerDebtAsync(customer));
        var statement = await owner.StatementAsync(customer);
        var unpaid = statement.Unpaid();
        Assert.Equal(older.Text("code"), Assert.Single(unpaid).Code);
        Assert.Equal(250_000m, unpaid.Sum(u => u.Unpaid));
        var newerEffect = (await owner.InvoiceAsync(newer.Id())).GetProperty("debtEffect");
        Assert.True(newerEffect.GetProperty("isPaid").GetBoolean());
        Assert.Equal(0m, newerEffect.Money("unpaid"));
    }

    // SALE-07 step 5 and step 10: a debt at exactly zero is no money owed back; below zero it is asked and notified.
    [Fact]
    public async Task A_debt_of_exactly_zero_is_not_money_owed_back_but_one_dong_less_is()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var customer = await NewCustomerAsync(sale);
        var sold = await sale.Manager.Client.SoldAsync(sale, sale.LotId, customerId: customer);
        await api.RecordPaymentAsync(customer, 0m + 50_000m);
        Assert.Equal(-50_000m, await owner.CustomerDebtAsync(customer));

        // Raising the total by exactly what the customer has overpaid brings the debt to 0: no question, no notice.
        var raised = (await owner.CorrectedAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 10m, 35_000m)], sale.LotId))).GetProperty("invoice");
        Assert.Equal(0m, await owner.CustomerDebtAsync(customer));
        Assert.Empty(await api.OwedBackNoticesAsync(sold.Id()));

        var question = await (await owner.CorrectAsync(sold.Id(), PriceCut(raised, 1m))).QuestionAsync();
        Assert.Equal(("MSG-SALE-17", "confirmOwedBack", 1m), (question.Code, question.Field, question.Body.Money("owedBackAmount")));
        Assert.Equal(0m, await owner.CustomerDebtAsync(customer));

        await owner.CorrectedAsync(sold.Id(), PriceCut(raised, 1m).With("confirmOwedBack", true));
        Assert.Equal(-1m, await owner.CustomerDebtAsync(customer));
        Assert.NotEmpty(await api.OwedBackNoticesAsync(sold.Id()));
        // BR-DEBT-05: with the debt at or below zero no invoice is unpaid.
        Assert.Empty((await owner.StatementAsync(customer)).GetProperty("unpaidInvoices").EnumerateArray());
    }

    // BR-SALE-10 (3) and (4): repeated corrections chain their totals; each is one audit event, oldest first in the detail and the statement.
    [Fact]
    public async Task Repeated_corrections_chain_their_totals_and_keep_each_event_once()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);

        var first = (await owner.CorrectedAsync(sold.Id(), PriceCut(sold, 10_000m))).GetProperty("invoice");
        var second = (await owner.CorrectedAsync(sold.Id(), DataEntry(first, [Line(sale.ProductId, sale.LotId, 8m, ListPrice)], sale.LotId).With("discount", 5_000m).With("otherCharges", 1_000m))).GetProperty("invoice");
        var third = (await owner.CorrectedAsync(sold.Id(), PriceCut(second, 20_000m))).GetProperty("invoice");

        Assert.Equal(240_000m, third.Money("subtotal"));
        Assert.Equal(30_000m, third.Money("priceCutTotal"));
        Assert.Equal(206_000m, third.Money("total"));
        Assert.Equal(206_000m, await owner.CustomerDebtAsync(sale.CustomerId));
        var corrections = third.GetProperty("corrections").EnumerateArray().ToList();
        Assert.Equal(["PriceCutAfterClaim", "DataEntryError", "PriceCutAfterClaim"], corrections.Select(c => c.Text("kind")).ToList());
        Assert.Equal([(300_000m, 290_000m), (290_000m, 226_000m), (226_000m, 206_000m)], corrections.Select(c => (c.Money("totalBefore"), c.Money("totalAfter"))).ToList());
        Assert.Equal(3, (await api.InvoiceAuditAsync(sold.Id(), SalesInvoiceCorrectionWriter.CorrectedAction)).Count);
        Assert.Equal(2, third.GetProperty("priceCuts").GetArrayLength());
    }

    // BR-SALE-06: the total is never below 0, whatever cut or discount is asked.
    [Fact]
    public async Task A_cut_or_a_discount_above_the_total_is_refused_and_a_cut_of_the_whole_total_leaves_zero()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);

        Assert.Equal(["Price cut amount"], await (await owner.CorrectAsync(sold.Id(), PriceCut(sold, 300_001m))).InvalidFieldsAsync());
        Assert.Equal(["Discount"], await (await owner.CorrectAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 10m)], sale.LotId).With("discount", 300_001m))).InvalidFieldsAsync());
        Assert.Equal(300_000m, await owner.CustomerDebtAsync(sale.CustomerId));

        var zero = (await owner.CorrectedAsync(sold.Id(), PriceCut(sold, 300_000m))).GetProperty("invoice");
        Assert.Equal(0m, zero.Money("total"));
        Assert.Equal(0m, await owner.CustomerDebtAsync(sale.CustomerId));
        Assert.Equal(["Price cut amount"], await (await owner.CorrectAsync(sold.Id(), PriceCut(zero, 1m))).InvalidFieldsAsync());
    }

    // A correction of the lines that would leave the kept price cuts above the new goods is refused, and names no change.
    [Fact]
    public async Task Lines_cut_below_the_earlier_price_cuts_are_refused()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        var cut = (await owner.CorrectedAsync(sold.Id(), PriceCut(sold, 200_000m))).GetProperty("invoice");

        var refused = await (await owner.CorrectAsync(sold.Id(), DataEntry(cut, [Line(sale.ProductId, sale.LotId, 1m, ListPrice)], sale.LotId))).InvalidFieldsAsync();

        Assert.Equal(["Discount"], refused);
        var saved = await owner.InvoiceAsync(sold.Id());
        Assert.Equal(100_000m, saved.Money("total"));
    }

    // Line amounts are rounded half up to the dong; a price cut keeps the total whole; a total equals its parts.
    [Fact]
    public async Task Rounding_keeps_the_total_whole_and_equal_to_its_parts()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);

        var corrected = (await owner.CorrectedAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 12.345m, 30_001m)], sale.LotId).With("otherCharges", 1m))).GetProperty("invoice");
        Assert.Equal(370_362m, corrected.GetProperty("lines")[0].Money("amount"));
        Assert.Equal(370_363m, corrected.Money("total"));

        var cut = (await owner.CorrectedAsync(sold.Id(), PriceCut(corrected, 3m))).GetProperty("invoice");
        Assert.Equal(cut.Money("subtotal") - cut.Money("discount") + cut.Money("otherCharges") - cut.Money("priceCutTotal"), cut.Money("total"));
        Assert.Equal(370_360m, cut.Money("total"));
    }

    // BR-SALE-07 (4): cash above the total lowered the debt; moving that invoice moves the whole payment, and the two debts stay right.
    [Fact]
    public async Task Moving_an_invoice_paid_with_cash_above_its_total_moves_the_whole_payment()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var manager = sale.Manager.Client;
        var oldProfile = await NewCustomerAsync(sale);
        var newProfile = await NewCustomerAsync(sale);
        await sale.PayLaterAsync(oldProfile, 10m);
        var sold = (await manager.SellAsync(Invoice(oldProfile, [Line(sale.ProductId, sale.OtherLotId, 10m)], Cash(400_000m, subtractExtraFromDebt: true), sale.OtherLotId))).GetProperty("invoice");
        Assert.Equal(200_000m, await owner.CustomerDebtAsync(oldProfile));

        var body = DataEntry(await owner.InvoiceAsync(sold.Id()), [Line(sale.ProductId, sale.OtherLotId, 10m, ListPrice)], sale.OtherLotId).With("customerId", newProfile);
        var question = await (await owner.CorrectAsync(sold.Id(), body)).QuestionAsync();
        Assert.Equal(("MSG-SALE-17", 100_000m), (question.Code, question.Body.Money("owedBackAmount")));

        await owner.CorrectedAsync(sold.Id(), body.With("confirmOwedBack", true));

        Assert.Equal(300_000m, await owner.CustomerDebtAsync(oldProfile));
        Assert.Equal(-100_000m, await owner.CustomerDebtAsync(newProfile));
        Assert.Equal((Guid?)newProfile, Assert.Single(await api.PaymentsOfAsync(sold.Id())).CustomerId);
        var oldStatement = await owner.StatementAsync(oldProfile);
        Assert.DoesNotContain(oldStatement.GetProperty("entries").EnumerateArray(), e => e.Id() == sold.Id());
        Assert.Contains((await owner.StatementAsync(newProfile)).GetProperty("entries").EnumerateArray(), e => e.Id() == sold.Id());
    }

    // BR-SALE-10 (2): "The total of an invoice that has no customer profile cannot be raised; the extra goods go on a new invoice" (SALE-07.0.E4, MSG-SALE-21).
    [Fact]
    public async Task Defect_a_walk_in_invoice_given_a_profile_in_the_same_correction_can_have_its_total_raised()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var profile = await NewCustomerAsync(sale);
        var sold = await sale.Manager.Client.SoldAsync(sale, sale.LotId, walkIn: true);

        var raised = await owner.CorrectAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 20m)], sale.LotId).With("customerId", profile));

        // Without the profile this very body is refused with MSG-SALE-21; with it the walk-in sale of 300,000 becomes a debt of 300,000 that nobody agreed.
        Assert.Equal(HttpStatusCode.Conflict, raised.StatusCode);
        Assert.Equal("MSG-SALE-21", (await raised.ReadAsync(HttpStatusCode.Conflict)).Text("code"));
        Assert.Equal(0m, await owner.CustomerDebtAsync(profile));
    }

    // The comment of the planner and SALE-07.2 give a profile only to a walk-in invoice with money to give back; a walk-in invoice with nothing paid gets a debt instead.
    [Fact]
    public async Task Defect_a_price_cut_attaches_a_profile_to_a_walk_in_invoice_that_has_no_money_to_give_back()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var profile = await NewCustomerAsync(sale);
        var sold = await sale.Manager.Client.SoldAsync(sale, sale.LotId, walkIn: true);
        var payment = Assert.Single(await api.PaymentsOfAsync(sold.Id()));
        await api.UpdateAsync<CustomerPayment>(payment.Id, p => (p.StatusId, p.CancelReason) = (StatusIds.CustomerPayment.Cancelled, "Recorded by mistake"));
        var current = await owner.InvoiceAsync(sold.Id());

        var answer = await owner.CorrectAsync(sold.Id(), PriceCut(current, 10_000m).With("customerId", profile));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal(0m, await owner.CustomerDebtAsync(profile));
    }

    // SALE-07.2: a walk-in invoice that stays paid in full keeps no profile and moves nothing; its total may go down to what was paid.
    [Fact]
    public async Task A_walk_in_correction_that_owes_nothing_back_needs_no_profile()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.Manager.Client.SoldAsync(sale, sale.LotId, walkIn: true);

        var same = (await owner.CorrectedAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 10m, ListPrice)], sale.LotId))).GetProperty("invoice");

        Assert.Equal(300_000m, same.Money("total"));
        Assert.Equal(JsonValueKind.Null, same.GetProperty("customer").ValueKind);
        Assert.Equal((Guid?)null, Assert.Single(await api.PaymentsOfAsync(sold.Id())).CustomerId);
    }

    // SALE-07.5: the unknown or merged profile is refused; naming the current profile is no change; a price cut never changes it.
    [Fact]
    public async Task The_profile_of_a_correction_must_exist_be_live_and_a_price_cut_never_changes_it()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        var keep = await NewCustomerAsync(sale);
        var gone = await NewCustomerAsync(sale);
        await api.UpdateAsync<Customer>(gone, c => c.MergedIntoCustomerId = keep);

        Assert.Equal(["Customer"], await (await owner.CorrectAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 10m)], sale.LotId).With("customerId", Guid.NewGuid()))).InvalidFieldsAsync());
        var merged = await (await owner.CorrectAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 10m)], sale.LotId).With("customerId", gone))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", merged.Text("code"));
        Assert.Equal(["Customer"], await (await owner.CorrectAsync(sold.Id(), PriceCut(sold, 1_000m).With("customerId", keep))).InvalidFieldsAsync());

        var named = (await owner.CorrectedAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 10m, ListPrice)], sale.LotId).With("customerId", sale.CustomerId))).GetProperty("invoice");
        Assert.Equal(sale.CustomerId, named.GetProperty("customer").Id());
        Assert.Equal(300_000m, await owner.CustomerDebtAsync(sale.CustomerId));
    }

    // BR-HARVEST-04: an invoice that lost a lot to another invoice can tick and untick it without touching that close.
    [Fact]
    public async Task A_mark_on_a_lot_closed_by_another_invoice_never_reopens_it()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var first = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice, notSoldWhole: false);
        Assert.Equal((Guid?)first.Id(), (await api.LotOfAsync(sale.LotId)).ClosedBySalesInvoiceId);
        var untick = (await owner.CorrectedAsync(first.Id(), DataEntry(first, [Line(sale.ProductId, sale.LotId, 10m, ListPrice)], sale.LotId))).GetProperty("invoice");
        Assert.Equal(StatusIds.HarvestLot.Reopened, (await api.LotOfAsync(sale.LotId)).StatusId);

        var closer = await sale.SoldOnDebtAsync(sale.LotId, 2m, ListPrice, notSoldWhole: false);
        Assert.Equal((Guid?)closer.Id(), (await api.LotOfAsync(sale.LotId)).ClosedBySalesInvoiceId);

        // The first invoice ticks the lot closed by the other one: nothing changes, and unticking it again reopens nothing.
        var tick = (await owner.CorrectedAsync(first.Id(), DataEntry(untick, [Line(sale.ProductId, sale.LotId, 10m, ListPrice)]))).GetProperty("invoice");
        Assert.True(tick.GetProperty("lines")[0].GetProperty("soldWholeLot").GetBoolean());
        await owner.CorrectedAsync(first.Id(), DataEntry(tick, [Line(sale.ProductId, sale.LotId, 10m, ListPrice)], sale.LotId));

        var lot = await api.LotOfAsync(sale.LotId);
        Assert.Equal((StatusIds.HarvestLot.Closed, (Guid?)closer.Id()), (lot.StatusId, lot.ClosedBySalesInvoiceId));
    }

    // BR-HARVEST-04: a lot a Zone Manager closed (no invoice) is reopened by no mark.
    [Fact]
    public async Task A_lot_closed_by_hand_is_never_reopened_by_a_mark()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice, notSoldWhole: false);
        await api.UpdateAsync<HarvestLot>(sale.LotId, l => (l.StatusId, l.ClosedBySalesInvoiceId, l.CloseReason, l.LeftoverKg) = (StatusIds.HarvestLot.Closed, null, HarvestLotCloseReason.Unsold, 5m));

        await owner.CorrectedAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 10m, ListPrice)], sale.LotId));

        var lot = await api.LotOfAsync(sale.LotId);
        Assert.Equal((StatusIds.HarvestLot.Closed, 5m), (lot.StatusId, lot.LeftoverKg));
    }

    // BR-HARVEST-04: the mark reopens, is added again, closes with leftover 0, and the kg sold of the lot follow the corrected lines.
    [Fact]
    public async Task A_mark_removed_then_added_again_reopens_then_closes_and_the_lot_figures_follow()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice, notSoldWhole: false);

        var unticked = (await owner.CorrectedAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 4m, ListPrice)], sale.LotId))).GetProperty("invoice");
        var reopened = await api.LotOfAsync(sale.LotId);
        Assert.Equal((StatusIds.HarvestLot.Reopened, (DateOnly?)PurchaseRequestTestKit.Today), (reopened.StatusId, reopened.ReopenedOn));

        await owner.CorrectedAsync(sold.Id(), DataEntry(unticked, [Line(sale.ProductId, sale.LotId, 4m, ListPrice)]));
        var closed = await api.LotOfAsync(sale.LotId);
        Assert.Equal((StatusIds.HarvestLot.Closed, 0m, (Guid?)sold.Id()), (closed.StatusId, closed.LeftoverKg, closed.ClosedBySalesInvoiceId));
        Assert.Equal(2, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == sale.LotId && e.Action == "HarvestLot.Closed")));
        using var scope = api.Services.CreateScope();
        var figures = await scope.ServiceProvider.GetRequiredService<OMTS.Application.Common.Interfaces.Persistence.IHarvestLotSalesQuery>().FiguresAsync([sale.LotId], CancellationToken.None);
        Assert.Equal(4m, figures[sale.LotId].InvoicedKg);
    }

    // SALE-07.3 has no server-side warning: the Withdrawn lot is sold as in SALE-05, where the warning also comes from the lots-for-sale list. Observed, not a failure.
    [Fact]
    public async Task A_withdrawn_lot_can_be_put_on_a_line_as_in_sale_05()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        await api.WithdrawAsync(sale.OtherLotId);

        var corrected = (await owner.CorrectedAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.OtherLotId, 10m, ListPrice)], sale.OtherLotId))).GetProperty("invoice");

        Assert.Equal(sale.OtherLotId, corrected.GetProperty("lines")[0].GetProperty("harvestLotId").GetGuid());
    }

    // The marks of a correction are a full list, as in SALE-05: a lot left out of notSoldWholeLotIds is ticked, so a lot still for sale closes. Observed: a body that omits the list closes it.
    [Fact]
    public async Task A_correction_that_leaves_out_the_marks_ticks_every_lot_it_keeps()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        Assert.Equal(StatusIds.HarvestLot.Open, (await api.LotOfAsync(sale.LotId)).StatusId);

        await owner.CorrectedAsync(sold.Id(), new Dictionary<string, object?>
        {
            ["kind"] = "DataEntryError", ["reason"] = Reason, ["rowVersion"] = sold.Text("rowVersion"), ["lines"] = new[] { Line(sale.ProductId, sale.LotId, 10m, ListPrice) },
        });

        Assert.Equal(StatusIds.HarvestLot.Closed, (await api.LotOfAsync(sale.LotId)).StatusId);
    }

    public static TheoryData<string, object> BadCorrectionBodies => new()
    {
        { "Reason", new Dictionary<string, object?> { ["reason"] = null } },
        { "Reason", new Dictionary<string, object?> { ["reason"] = "   " } },
        { "Reason", new Dictionary<string, object?> { ["reason"] = new string('r', 1001) } },
        { "RowVersion", new Dictionary<string, object?> { ["rowVersion"] = null } },
        { "RowVersion", new Dictionary<string, object?> { ["rowVersion"] = "not base64 !" } },
        { "Kind", new Dictionary<string, object?> { ["kind"] = null } },
        { "Kind", new Dictionary<string, object?> { ["kind"] = 99 } },
        { "Price cut amount", new Dictionary<string, object?> { ["priceCutAmount"] = 0m } },
        { "Price cut amount", new Dictionary<string, object?> { ["priceCutAmount"] = -1m } },
        { "Price cut amount", new Dictionary<string, object?> { ["priceCutAmount"] = 1.5m } },
        { "Price cut amount", new Dictionary<string, object?> { ["priceCutAmount"] = null } },
        { "Kind", new Dictionary<string, object?> { ["discount"] = 1m } },
        { "Kind", new Dictionary<string, object?> { ["otherCharges"] = 1m } },
        { "Kind", new Dictionary<string, object?> { ["notSoldWholeLotIds"] = Array.Empty<Guid>() } },
    };

    [Theory]
    [MemberData(nameof(BadCorrectionBodies))]
    public async Task A_bad_field_is_answered_with_its_name_and_changes_nothing(string field, object changes)
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        var body = PriceCut(sold, 1_000m);
        foreach (var (key, value) in (Dictionary<string, object?>)changes)
        {
            body[key] = value;
        }

        Assert.Contains(field, await (await owner.CorrectAsync(sold.Id(), body)).InvalidFieldsAsync());
        Assert.Equal(300_000m, await owner.CustomerDebtAsync(sale.CustomerId));
        Assert.Empty(await api.InvoiceAuditAsync(sold.Id(), SalesInvoiceCorrectionWriter.CorrectedAction));
    }

    [Fact]
    public async Task A_bad_data_entry_correction_names_every_field_and_changes_nothing()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);

        var invalid = await (await owner.CorrectAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 0m), Line(sale.ProductId, sale.LotId, -1m, 0m)]).With("reason", null).With("discount", -1m).With("otherCharges", 1.5m))).InvalidFieldsAsync();

        Assert.Equal(["Reason", "Line 1 kg", "Line 2 kg", "Line 2 unit price", "Discount", "Other charges"], invalid);
        var strangers = await (await owner.CorrectAsync(sold.Id(), DataEntry(sold, [Line(Guid.NewGuid(), sale.LotId, 1m)]))).InvalidFieldsAsync();
        Assert.Equal(["Line 1 product"], strangers);
        Assert.Equal(["Line 1 harvest lot"], await (await owner.CorrectAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, Guid.NewGuid(), 1m)]))).InvalidFieldsAsync());
        Assert.Equal(["Sold the whole lot"], await (await owner.CorrectAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 1m)], Guid.NewGuid()))).InvalidFieldsAsync());
        Assert.Equal(300_000m, await owner.CustomerDebtAsync(sale.CustomerId));
    }

    // An unknown invoice, or the id of another record, is a 404; an unknown kind string does not reach the service as a 500.
    [Fact]
    public async Task An_unknown_or_wrong_kind_of_id_and_a_kind_of_another_vocabulary_are_refused_without_a_500()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);

        Assert.Equal(HttpStatusCode.NotFound, (await owner.CorrectAsync(sale.LotId, PriceCut(sold, 1_000m))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.CorrectAsync(Guid.NewGuid(), PriceCut(sold, 1_000m))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.CancelInvoiceAsync(Guid.NewGuid(), Cancellation(sold))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.CorrectAsync(sold.Id(), PriceCut(sold, 1_000m).With("kind", "Nonsense"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/sales-invoices/{sold.Id()}/correction", "just text")).StatusCode);
    }

    // SALE-07.0.E2 for values that do not fit: a line amount above decimal can hold must be a field error, as every other bad entry, not a 500.
    [Fact]
    public async Task Defect_a_line_amount_too_large_to_compute_answers_500_not_msg_gen_01()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);

        var answer = await owner.CorrectAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 999_999_999_999_999.999m, 999_999_999_999_999_999m)], sale.LotId));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
    }

    // The correction leaves the invoice's code, date, payment term, QR decision and payments.
    [Fact]
    public async Task A_correction_keeps_the_code_the_date_the_term_and_the_payments()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = (await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(100_000m, "PayLater", payLaterOwnerAgreed: true), sale.LotId))).GetProperty("invoice");

        var corrected = (await owner.CorrectedAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.OtherLotId, 3m, 40_000m)], sale.OtherLotId))).GetProperty("invoice");

        Assert.Equal((sold.Text("code"), sold.Text("invoiceDate"), sold.Text("paymentTerm")), (corrected.Text("code"), corrected.Text("invoiceDate"), corrected.Text("paymentTerm")));
        Assert.Equal(1, corrected.GetProperty("payments").GetArrayLength());
        Assert.Equal(20_000m, await owner.CustomerDebtAsync(sale.CustomerId));
    }

    // SALE-09.1 step 2: the print of an invoice shows the corrections and, once cancelled, the cancel date and reason, as the detail now does.
    [Fact]
    public async Task Defect_the_print_of_an_invoice_shows_neither_its_corrections_nor_its_cancellation()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        var corrected = (await owner.CorrectedAsync(sold.Id(), PriceCut(sold, 10_000m).With("reason", "DEMO claim accepted"))).GetProperty("invoice");
        await owner.CancelledAsync(sold.Id(), Cancellation(corrected).With("reason", "DEMO sale did not happen"));

        var text = await PrintedTextAsync(owner, sold.Id());

        Assert.Contains("DEMO claim accepted", text);
        Assert.Contains("DEMO sale did not happen", text);
    }

    // BR-DEBT-04 / DEBT-03.2: the file of the statement shows what the screen shows, the corrections under each invoice included.
    [Fact]
    public async Task Defect_the_statement_file_leaves_out_the_corrections_under_each_invoice()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        await owner.CorrectedAsync(sold.Id(), PriceCut(sold, 10_000m).With("reason", "DEMO claim accepted"));

        var file = await owner.GetAsync($"/api/customer-debts/export?customerId={sale.CustomerId}");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        using var workbook = new XLWorkbook(await file.Content.ReadAsStreamAsync());

        var cells = workbook.Worksheets.SelectMany(s => s.CellsUsed()).Select(c => c.GetString());
        Assert.Contains(cells, c => c.Contains("DEMO claim accepted"));
    }

    private static async Task<string> PrintedTextAsync(HttpClient client, Guid invoiceId)
    {
        var file = await client.PrintInvoiceAsync(invoiceId);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        using var workbook = new XLWorkbook(await file.Content.ReadAsStreamAsync());
        return string.Join(" | ", workbook.Worksheets.SelectMany(s => s.CellsUsed()).Select(c => c.GetString()));
    }
}
