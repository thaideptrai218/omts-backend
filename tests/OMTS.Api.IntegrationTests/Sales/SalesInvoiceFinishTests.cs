using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-05 steps 9-11 and AC-12: the QR question (MSG-SALE-07), the QR code only with a Published lot, the first sending (SALE-06) and its result.</summary>
public class SalesInvoiceFinishTests
{
    private static string Phone(JsonElement invoice) => invoice.GetProperty("customer").Text("phoneNumber")!;

    // AC-12: of the two lots of the invoice one is Published, the other Not published: one QR code, the page lists the Published lot, the SMS carries the link.
    [Fact]
    public async Task Ac_12_the_invoice_gets_one_qr_code_with_one_published_lot_and_is_sent_with_its_link()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.PublishAsync(sale.LotId);
        var invoice = (await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m), Line(sale.ProductId, sale.OtherLotId, 5m)], Cash(450_000m))))
            .GetProperty("invoice");

        var asked = await (await sale.Manager.Client.FinishAsync(invoice.Id(), null)).QuestionAsync();
        Assert.Equal(("MSG-SALE-07", "wantsQrCode"), (asked.Code, asked.Field));
        Assert.Equal($"Does the trader want the QR code of invoice {invoice.Text("code")}, to see where the oysters come from?", asked.Body.Text("detail"));
        Assert.Empty(await api.InvoiceSendingsAsync(invoice.Id()));

        var finished = await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: true);

        var qrUrl = finished.Text("qrUrl")!;
        var token = finished.GetProperty("invoice").Text("qrToken")!;
        Assert.Matches("^[A-Za-z0-9_-]{22}$", token);
        Assert.Equal($"http://localhost:3000/origin/{token}", qrUrl);
        var lots = finished.GetProperty("lots").EnumerateArray().ToDictionary(l => l.GetProperty("harvestLotId").GetGuid(), l => l.Text("originState"));
        Assert.Equal(("Published", "NotPublished"), (lots[sale.LotId], lots[sale.OtherLotId]));
        Assert.Equal(("Sent", StatusIds.MessageDelivery.Sent), (finished.Text("deliveryStatus"), finished.GetProperty("deliveryStatusId").GetInt32()));
        Assert.Equal(JsonValueKind.Null, finished.GetProperty("messageCode").ValueKind);

        // SALE-06 steps 2-5: MSG-SALE-15 at the latest values, with the origin link; the sending is kept, outside any list.
        var sms = Assert.Single(api.SmsTo(Phone(invoice)));
        Assert.EndsWith($"15 kg, 450,000 VND, paid 450,000 VND. Origin: {qrUrl}", sms.Text);
        Assert.Contains($"invoice {invoice.Text("code")} of ", sms.Text);
        var row = Assert.Single(await api.InvoiceSendingsAsync(invoice.Id()));
        Assert.Equal(("MSG-SALE-15", StatusIds.MessageDelivery.Sent, Phone(invoice), (Guid?)null), (row.MessageCode, row.DeliveryStatusId, row.PhoneNumber, row.RecipientAccountId));
        var view = finished.GetProperty("invoice");
        Assert.Equal("Sent", view.Text("deliveryStatus"));
        Assert.Equal(("Sms", "Sent"), (view.GetProperty("sendings")[0].Text("channel"), view.GetProperty("sendings")[0].Text("deliveryStatus")));

        // POST-4 and GBR-02: the QR code is kept on the invoice and the decision is audited, never with its token.
        var saved = await api.QueryAsync(db => db.SalesInvoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id()));
        Assert.Equal(token, saved.QrToken);
        Assert.NotNull(saved.QrCreatedAt);
        var audit = await api.QueryAsync(db => db.AuditEvents.AsNoTracking().SingleAsync(e => e.RecordId == invoice.Id() && e.Action == "SalesInvoice.SaleFinished"));
        Assert.DoesNotContain(token, audit.After ?? string.Empty);
        Assert.Contains("\"QrCodeCreated\":true", audit.After);
        Assert.Equal(sale.ZoneId, audit.ZoneId);
        var read = await (await sale.Owner.Client.GetAsync($"/api/sales-invoices/{invoice.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(view.GetRawText(), read.GetRawText());
    }

    // BR-TRACE-02, SALE-05.10 and SALE-05.11: no Yes, or no lot Published (Not published or Withdrawn), means no QR code, now or later; the invoice is still sent.
    [Fact]
    public async Task No_qr_code_without_a_yes_or_without_a_published_lot_and_the_end_of_the_sale_happens_once()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.WithdrawAsync(sale.OtherLotId);
        var notPublished = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var withdrawn = await sale.Manager.Client.SoldAsync(sale, sale.OtherLotId);
        await AssertNoQrCodeAsync(notPublished, wants: true);
        await AssertNoQrCodeAsync(withdrawn, wants: true);
        await api.PublishAsync(sale.LotId);
        await AssertNoQrCodeAsync(await sale.Manager.Client.SoldAsync(sale, sale.LotId), wants: false);

        // Once only: the lot of the first invoice is Published now, but a second end of the sale is refused and creates no QR code.
        var again = await (await sale.Owner.Client.FinishAsync(notPublished.Id(), true)).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-09", $"Sales invoice {notPublished.Text("code")} is already finished and cannot be used for this action."), (again.Text("code"), again.Text("detail")));
        Assert.Equal(0, await api.QueryAsync(db => db.SalesInvoices.CountAsync(i => i.QrToken != null)));
        Assert.Single(await api.InvoiceSendingsAsync(notPublished.Id()));

        async Task AssertNoQrCodeAsync(JsonElement invoice, bool wants)
        {
            var finished = await sale.Manager.Client.FinishedAsync(invoice.Id(), wants);
            Assert.Equal(JsonValueKind.Null, finished.GetProperty("qrUrl").ValueKind);
            Assert.Equal(JsonValueKind.Null, finished.GetProperty("invoice").GetProperty("qrToken").ValueKind);
            Assert.Equal("Sent", finished.Text("deliveryStatus"));
            Assert.DoesNotContain("Origin:", api.SmsTo(Phone(invoice))[0].Text);
        }
    }

    // SALE-06.0.E1 and SALE-05.12: a walk-in customer or a profile without a phone gets nothing, Not sent with MSG-SALE-16; the QR code is still made for the screen.
    [Fact]
    public async Task A_walk_in_customer_or_a_profile_without_a_phone_is_not_sent_and_msg_sale_16_says_why()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.PublishAsync(sale.LotId);
        var walkIn = await sale.Manager.Client.SoldAsync(sale, sale.LotId, walkIn: true);
        var noPhone = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        await api.UpdateAsync<Customer>(sale.CustomerId, c => c.PhoneNumber = null);

        var walkInAnswer = await sale.Manager.Client.FinishedAsync(walkIn.Id(), wantsQrCode: true);
        var noPhoneAnswer = await sale.Manager.Client.FinishedAsync(noPhone.Id(), wantsQrCode: false);

        Assert.Equal(("Not sent", "MSG-SALE-16", $"Invoice {walkIn.Text("code")} is not sent: it has no customer profile. Please show the invoice to the trader on your screen."),
            (walkInAnswer.Text("deliveryStatus"), walkInAnswer.Text("messageCode"), walkInAnswer.Text("message")));
        Assert.NotNull(walkInAnswer.Text("qrUrl"));
        Assert.Equal(("Not sent", "MSG-SALE-16", $"Invoice {noPhone.Text("code")} is not sent: the customer profile has no phone number. Please show the invoice to the trader on your screen."),
            (noPhoneAnswer.Text("deliveryStatus"), noPhoneAnswer.Text("messageCode"), noPhoneAnswer.Text("message")));
        // BR-SALE-09: nothing was sent, so no row; the invoice shows Not sent, as one with no sending at all.
        Assert.Empty(await api.InvoiceSendingsAsync(walkIn.Id()));
        Assert.Empty(await api.InvoiceSendingsAsync(noPhone.Id()));
        Assert.Equal(("Not sent", 0), (walkInAnswer.GetProperty("invoice").Text("deliveryStatus"), walkInAnswer.GetProperty("invoice").GetProperty("sendings").GetArrayLength()));
        var resent = await (await sale.Owner.Client.ResendAsync(walkIn.Id())).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("Not sent", "MSG-SALE-16"), (resent.Text("deliveryStatus"), resent.Text("messageCode")));
    }

    // The trader of a linked profile is the recipient of the row; the part to pay names the receiving account (MSG-SALE-15).
    [Fact]
    public async Task A_pay_later_invoice_tells_the_part_to_pay_with_the_receiving_account_and_keeps_the_trader_account_on_the_row()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.SetReceivingAccountAsync();
        var trader = await api.TraderAsync();
        var invoice = (await sale.Owner.Client.SellAsync(Invoice(trader.Profile.Id, [Line(sale.ProductId, sale.LotId, 100m)], Cash(1_000_000m, rest: "PayLater", payLaterOwnerAgreed: true), sale.LotId)))
            .GetProperty("invoice");

        await sale.Owner.Client.FinishedAsync(invoice.Id(), wantsQrCode: false);

        var sms = Assert.Single(api.SmsTo(trader.Profile.PhoneNumber!));
        Assert.EndsWith(
            $"100 kg, 3,000,000 VND, paid 1,000,000 VND; to pay 2,000,000 VND to account {SalesInvoiceSendingTestKit.DemoAccountNumber} ({SalesInvoiceSendingTestKit.DemoBankName}), transfer content {invoice.Text("code")}.",
            sms.Text);
        var row = Assert.Single(await api.InvoiceSendingsAsync(invoice.Id()));
        Assert.Equal(trader.Account.Id, row.RecipientAccountId);
        Assert.Contains($"\"bank_account\":\"{SalesInvoiceSendingTestKit.DemoAccountNumber}\"", row.MessageParams);
        // A sending is never in the trader's notification list (BR-SALE-09).
        Assert.DoesNotContain(await api.QueryAsync(db => db.Notifications.Where(n => n.RecipientAccountId == trader.Account.Id).ToListAsync()), n => n.Channel == Domain.Enums.NotificationChannel.InApp && n.Type == row.Type);
    }
}
