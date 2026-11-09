using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-07: a price cut after a claim (AC-08) and a data entry correction, the lots whose mark changed, the walk-in rules, the profile change, money owed back and the message sent again.</summary>
public class SalesInvoiceCorrectionTests
{
    // AC-08: 10,000,000 VND on Pay later, a price cut of 500,000: 9,500,000, and the debt goes down by 500,000 once.
    [Fact]
    public async Task A_price_cut_lowers_the_total_and_the_debt_once_and_leaves_the_lines_alone()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 200m, 50_000m);
        Assert.Equal(10_000_000m, sold.Money("total"));
        await owner.FinishedAsync(sold.Id(), wantsQrCode: false);
        var before = await owner.InvoiceAsync(sold.Id());

        var answer = await owner.CorrectedAsync(sold.Id(), PriceCut(before, 500_000m));

        var invoice = answer.GetProperty("invoice");
        Assert.Equal((9_500_000m, 500_000m), (invoice.Money("total"), invoice.Money("priceCutTotal")));
        var line = invoice.GetProperty("lines")[0];
        Assert.Equal((200m, 50_000m, 10_000_000m), (line.Money("kg"), line.Money("unitPrice"), line.Money("amount")));
        var cut = invoice.GetProperty("priceCuts")[0];
        Assert.Equal((500_000m, Reason), (cut.Money("amount"), cut.Text("reason")));
        Assert.Equal(9_500_000m, await owner.CustomerDebtAsync(sale.CustomerId));
        Assert.Equal("Sent", answer.Text("deliveryStatus"));

        var correction = invoice.GetProperty("corrections")[0];
        Assert.Equal(("PriceCutAfterClaim", Reason, 10_000_000m, 9_500_000m), (correction.Text("kind"), correction.Text("reason"), correction.Money("totalBefore"), correction.Money("totalAfter")));
        Assert.Equal(("PriceCutAfterClaim", Reason), (invoice.Text("lastCorrectionKind"), invoice.Text("lastCorrectionReason")));
        Assert.Single(await api.InvoiceAuditAsync(sold.Id(), SalesInvoiceCorrectionWriter.CorrectedAction));

        // Several price cuts add up; the debt still moves only by what was entered.
        var second = await owner.CorrectedAsync(sold.Id(), PriceCut(invoice, 500_000m));
        Assert.Equal((9_000_000m, 1_000_000m), (second.GetProperty("invoice").Money("total"), second.GetProperty("invoice").Money("priceCutTotal")));
        Assert.Equal(9_000_000m, await owner.CustomerDebtAsync(sale.CustomerId));
    }

    [Fact]
    public async Task Only_the_owner_corrects_and_a_cancelled_invoice_is_refused_as_it_is_not_offered()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        var zone = await api.ZoneEntityAsync(sale.ZoneId);
        var accountant = await api.StaffAsync(zone, StaffRole.Accountant);

        Assert.Equal("MSG-GEN-08", (await (await sale.Manager.Client.CorrectAsync(sold.Id(), PriceCut(sold, 1_000m))).ReadAsync(HttpStatusCode.Forbidden)).Text("code"));
        Assert.Equal("MSG-GEN-08", (await (await accountant.Client.CorrectAsync(sold.Id(), PriceCut(sold, 1_000m))).ReadAsync(HttpStatusCode.Forbidden)).Text("code"));

        await sale.Owner.Client.CancelledAsync(sold.Id(), Cancellation(sold));

        var refused = await (await sale.Owner.Client.CorrectAsync(sold.Id(), PriceCut(sold, 1_000m))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", refused.Text("code"));
    }

    [Fact]
    public async Task The_reason_and_the_fields_of_the_kind_are_required()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);

        Assert.Equal(["Reason"], await (await owner.CorrectAsync(sold.Id(), PriceCut(sold, 1_000m).With("reason", " "))).InvalidFieldsAsync());
        Assert.Equal(["Price cut amount"], await (await owner.CorrectAsync(sold.Id(), PriceCut(sold, 0m))).InvalidFieldsAsync());
        Assert.Equal(["Price cut amount"], await (await owner.CorrectAsync(sold.Id(), PriceCut(sold, sold.Money("total") + 1m))).InvalidFieldsAsync());
        Assert.Equal(["Lines"], await (await owner.CorrectAsync(sold.Id(), DataEntry(sold, []))).InvalidFieldsAsync());
        Assert.Equal(["Kind"], await (await owner.CorrectAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 5m)]).With("priceCutAmount", 1_000m))).InvalidFieldsAsync());
        Assert.Equal(300_000m, await owner.CustomerDebtAsync(sale.CustomerId));
    }

    // BR-SALE-10 (2), SALE-07 step 8: the lines change in place, the debt follows the new total.
    [Fact]
    public async Task A_data_entry_correction_replaces_the_lines_and_the_total_and_keeps_the_code_and_payments()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = (await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(100_000m, "PayLater", payLaterOwnerAgreed: true), sale.LotId))).GetProperty("invoice");
        var code = sold.Text("code");
        Assert.Equal(200_000m, await owner.CustomerDebtAsync(sale.CustomerId));

        var corrected = (await owner.CorrectedAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 8m, 25_000m)], sale.LotId).With("discount", 10_000m).With("otherCharges", 5_000m))).GetProperty("invoice");

        Assert.Equal((code, 195_000m, 200_000m, 10_000m, 5_000m), (corrected.Text("code"), corrected.Money("total"), corrected.Money("subtotal"), corrected.Money("discount"), corrected.Money("otherCharges")));
        Assert.Equal(1, corrected.GetProperty("payments").GetArrayLength());
        Assert.Equal(95_000m, await owner.CustomerDebtAsync(sale.CustomerId));
        var audit = Assert.Single(await api.InvoiceAuditAsync(sold.Id(), SalesInvoiceCorrectionWriter.CorrectedAction));
        Assert.Contains("300000", audit.Before);
        Assert.Contains("195000", audit.After);
        Assert.Equal(Reason, audit.Reason);
    }

    // Lines added and removed; a mark removed reopens the lot this invoice closed; a mark added closes a lot still for sale.
    [Fact]
    public async Task A_mark_removed_reopens_the_lot_this_invoice_closed_and_a_mark_added_closes_a_lot_for_sale()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = (await owner.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m), Line(sale.ProductId, sale.OtherLotId, 5m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.OtherLotId))).GetProperty("invoice");
        Assert.Equal(StatusIds.HarvestLot.Closed, (await api.LotOfAsync(sale.LotId)).StatusId);

        var corrected = (await owner.CorrectedAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 10m), Line(sale.ProductId, sale.OtherLotId, 5m)], sale.LotId))).GetProperty("invoice");

        var reopened = await api.LotOfAsync(sale.LotId);
        Assert.Equal((StatusIds.HarvestLot.Reopened, (Guid?)null), (reopened.StatusId, reopened.ClosedBySalesInvoiceId));
        var closed = await api.LotOfAsync(sale.OtherLotId);
        Assert.Equal((StatusIds.HarvestLot.Closed, (Guid?)sold.Id()), (closed.StatusId, closed.ClosedBySalesInvoiceId));

        // Removing the line of a lot this invoice closed also reopens it.
        var shorter = (await owner.CorrectedAsync(sold.Id(), DataEntry(corrected, [Line(sale.ProductId, sale.LotId, 3m)], sale.LotId))).GetProperty("invoice");
        Assert.Single(shorter.GetProperty("lines").EnumerateArray());
        Assert.Equal(StatusIds.HarvestLot.Reopened, (await api.LotOfAsync(sale.OtherLotId)).StatusId);
    }

    // BR-HARVEST-04: an unchanged mark never closes again a lot reopened since; a lot closed by another invoice is left alone.
    [Fact]
    public async Task An_unchanged_mark_leaves_the_lot_alone_and_another_invoices_close_is_not_undone()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice, notSoldWhole: false);
        await api.UpdateAsync<OMTS.Domain.Entities.Harvest.HarvestLot>(sale.LotId, l => (l.StatusId, l.ClosedBySalesInvoiceId, l.CloseDate, l.CloseReason, l.LeftoverKg) = (StatusIds.HarvestLot.Reopened, null, null, null, null));

        await owner.CorrectedAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 8m)]));

        Assert.Equal(StatusIds.HarvestLot.Reopened, (await api.LotOfAsync(sale.LotId)).StatusId);

        var other = await sale.SoldOnDebtAsync(sale.OtherLotId, 10m, ListPrice, notSoldWhole: false);
        var refused = await owner.CorrectAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.OtherLotId, 8m)], sale.OtherLotId));
        Assert.Equal("MSG-GEN-09", (await refused.ReadAsync(HttpStatusCode.Conflict)).Text("code"));
        Assert.Equal((Guid?)other.Id(), (await api.LotOfAsync(sale.OtherLotId)).ClosedBySalesInvoiceId);
    }

    [Fact]
    public async Task A_lot_already_on_the_invoice_stays_even_when_closed_and_a_product_discontinued_since_stays()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice, notSoldWhole: false);
        await api.UpdateAsync<Product>(sale.ProductId, p => p.StatusId = StatusIds.Usage.Discontinued);

        var corrected = await owner.CorrectedAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 9m, ListPrice)]));

        Assert.Equal(270_000m, corrected.GetProperty("invoice").Money("total"));
        Assert.Equal(StatusIds.HarvestLot.Closed, (await api.LotOfAsync(sale.LotId)).StatusId);
    }

    [Fact]
    public async Task A_walk_in_invoice_never_raises_its_total_and_needs_a_profile_when_money_is_owed_back()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.Manager.Client.SoldAsync(sale, sale.LotId, walkIn: true);

        var raised = await (await owner.CorrectAsync(sold.Id(), DataEntry(sold, [Line(sale.ProductId, sale.LotId, 11m)], sale.LotId))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-SALE-21", raised.Text("code"));

        var (code, field, question) = await (await owner.CorrectAsync(sold.Id(), PriceCut(sold, 100_000m))).QuestionAsync();
        Assert.Equal(("MSG-SALE-19", "customerId", 100_000m), (code, field, question.Money("owedBackAmount")));

        var body = PriceCut(sold, 100_000m).With("customerId", sale.CustomerId);
        var owedBack = await (await owner.CorrectAsync(sold.Id(), body)).QuestionAsync();
        Assert.Equal(("MSG-SALE-17", "confirmOwedBack", 100_000m), (owedBack.Code, owedBack.Field, owedBack.Body.Money("owedBackAmount")));
        Assert.Equal(0m, await owner.CustomerDebtAsync(sale.CustomerId));

        var corrected = (await owner.CorrectedAsync(sold.Id(), body.With("confirmOwedBack", true))).GetProperty("invoice");

        Assert.Equal(sale.CustomerId, corrected.GetProperty("customer").Id());
        Assert.Equal(-100_000m, await owner.CustomerDebtAsync(sale.CustomerId));
        var payment = Assert.Single(await api.PaymentsOfAsync(sold.Id()));
        Assert.Equal((Guid?)sale.CustomerId, payment.CustomerId);
    }

    // SALE-07.5: another profile takes the invoice and the payments that name it; both debts stay right and the message goes to the new phone.
    [Fact]
    public async Task Changing_the_profile_moves_the_payments_and_both_debts_and_the_message()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var other = await owner.CreateCustomerAsync();
        var sold = (await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(100_000m, "PayLater", payLaterOwnerAgreed: true), sale.LotId))).GetProperty("invoice");
        await sale.Manager.Client.FinishedAsync(sold.Id(), wantsQrCode: false);
        var current = await owner.InvoiceAsync(sold.Id());

        await owner.CorrectedAsync(sold.Id(), DataEntry(current, [Line(sale.ProductId, sale.LotId, 10m, ListPrice)], sale.LotId).With("customerId", other.Id()));

        Assert.Equal(0m, await owner.CustomerDebtAsync(sale.CustomerId));
        Assert.Equal(200_000m, await owner.CustomerDebtAsync(other.Id()));
        Assert.Equal((Guid?)other.Id(), Assert.Single(await api.PaymentsOfAsync(sold.Id())).CustomerId);
        var sms = api.SmsTo(other.Text("phoneNumber")!);
        Assert.Contains("(corrected)", sms[0].Text);
        var audit = Assert.Single(await api.InvoiceAuditAsync(sold.Id(), SalesInvoiceCorrectionWriter.CorrectedAction));
        Assert.Contains(sale.CustomerId.ToString(), audit.Before, StringComparison.OrdinalIgnoreCase);
    }

    // MSG-SALE-17 asks; MSG-SALE-18 tells the Owners and, with a request to pay back, the Accountants.
    [Fact]
    public async Task A_debt_below_zero_is_asked_about_and_notified_to_owner_and_accountant()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var zone = await api.ZoneEntityAsync(sale.ZoneId);
        var accountant = await api.StaffAsync(zone, StaffRole.Accountant);
        var sold = await sale.Manager.Client.SoldAsync(sale, sale.LotId);

        var question = await (await owner.CorrectAsync(sold.Id(), PriceCut(sold, 50_000m))).QuestionAsync();
        Assert.Equal(("MSG-SALE-17", "confirmOwedBack", 50_000m), (question.Code, question.Field, question.Body.Money("owedBackAmount")));

        await owner.CorrectedAsync(sold.Id(), PriceCut(sold, 50_000m).With("confirmOwedBack", true));

        var notices = await api.OwedBackNoticesAsync(sold.Id());
        Assert.Equal("MSG-SALE-18", notices[0].MessageCode);
        Assert.DoesNotContain("Please pay the money back", notices.Single(n => n.RecipientAccountId == sale.Owner.Account.Id).Body);
        Assert.Contains("Please pay the money back", notices.Single(n => n.RecipientAccountId == accountant.Account.Id).Body);
    }

    // GFR-06: a provider that refuses leaves the correction saved, shown as Not sent, and Resend offered.
    [Fact]
    public async Task A_message_the_service_refuses_keeps_the_correction_and_shows_not_sent()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        await owner.FinishedAsync(sold.Id(), wantsQrCode: false);
        var current = await owner.InvoiceAsync(sold.Id());
        api.RefuseSmsTo(await api.PhoneOfAsync(sale.CustomerId));

        var answer = await owner.CorrectedAsync(sold.Id(), PriceCut(current, 10_000m));

        Assert.Equal("Not sent", answer.Text("deliveryStatus"));
        Assert.Equal(290_000m, answer.GetProperty("invoice").Money("total"));
        Assert.Contains(SalesInvoiceAction.Resend.ToString(), answer.GetProperty("invoice").Actions());
    }

    // BR-SALE-08: before the end of the sale nothing has been sent, so the correction sends nothing: the QR question is still open.
    [Fact]
    public async Task A_correction_before_the_end_of_the_sale_sends_nothing()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);

        var answer = await sale.Owner.Client.CorrectedAsync(sold.Id(), PriceCut(sold, 10_000m));

        Assert.Equal(JsonValueKind.Null, answer.GetProperty("invoice").GetProperty("qrDecidedAt").ValueKind);
        Assert.Empty(await api.InvoiceSendingsAsync(sold.Id()));
    }
}
