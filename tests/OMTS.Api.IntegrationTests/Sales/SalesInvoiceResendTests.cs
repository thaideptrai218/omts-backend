using System.Net;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-06.1 and BR-SALE-09: Resend at the latest values next to the earlier sendings, not while Sending, by the Zone Manager of the zone and the Owner only; GFR-06.</summary>
public class SalesInvoiceResendTests
{
    // GFR-06: the service refuses the message; the invoice is kept, the row says Not sent, and a Resend later is Sent, next to the first sending.
    [Fact]
    public async Task A_message_the_service_refuses_keeps_the_invoice_says_not_sent_and_a_resend_is_kept_next_to_it()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var phone = invoice.GetProperty("customer").Text("phoneNumber")!;
        api.RefuseSmsTo(phone);

        var finished = await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: false);

        Assert.Equal(("Not sent", JsonNull), (finished.Text("deliveryStatus"), finished.GetProperty("messageCode").ValueKind));
        Assert.Equal(StatusIds.SalesInvoice.Completed, await api.QueryAsync(db => db.SalesInvoices.Where(i => i.Id == invoice.Id()).Select(i => i.StatusId).SingleAsync()));
        Assert.Equal(1, await api.QueryAsync(db => db.CustomerPayments.CountAsync(p => p.InvoiceId == invoice.Id())));
        var refused = Assert.Single(await api.InvoiceSendingsAsync(invoice.Id()));
        Assert.Equal((StatusIds.MessageDelivery.NotSent, "FAKE_CONFIGURED_FAILURE"), (refused.DeliveryStatusId, refused.ProviderErrorCode));

        api.AllowSmsTo(phone);
        var resent = await (await sale.Owner.Client.ResendAsync(invoice.Id())).ReadAsync(HttpStatusCode.OK);

        Assert.Equal("Sent", resent.Text("deliveryStatus"));
        var sendings = resent.GetProperty("invoice").GetProperty("sendings").EnumerateArray().Select(s => s.Text("deliveryStatus")).ToList();
        Assert.Equal(["Not sent", "Sent"], sendings);
        Assert.Equal("Sent", resent.GetProperty("invoice").Text("deliveryStatus"));
        Assert.Single(api.SmsTo(phone));
    }

    // SALE-06.1 step 2: the message carries the latest values, e.g. a payment cancelled since the first sending.
    [Fact]
    public async Task A_resend_carries_the_latest_values_of_the_invoice()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId, kg: 10m);
        var phone = invoice.GetProperty("customer").Text("phoneNumber")!;
        await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: false);
        var paymentId = invoice.GetProperty("payments")[0].Id();
        await api.UpdateAsync<CustomerPayment>(paymentId, p => p.StatusId = StatusIds.CustomerPayment.Cancelled);

        (await sale.Manager.Client.ResendAsync(invoice.Id())).EnsureSuccessStatusCode();

        var texts = api.SmsTo(phone).Select(m => m.Text).ToList();
        Assert.Equal(2, texts.Count);
        Assert.EndsWith("300,000 VND, paid 0 VND; to pay 300,000 VND.", texts[0]);
        Assert.EndsWith("300,000 VND, paid 300,000 VND.", texts[1]);
    }

    // PRE-2: not while the latest sending is Sending; a Sending row left by a stop long ago blocks nothing.
    [Fact]
    public async Task Resend_is_refused_while_the_latest_sending_is_sending_and_a_stale_sending_reads_not_sent()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: false);
        var firstId = Assert.Single(await api.InvoiceSendingsAsync(invoice.Id())).Id;
        var row = new Notification
        {
            Channel = NotificationChannel.Sms, Type = SalesInvoiceMessages.MessageType, Title = SalesInvoiceMessages.Title, RelatedRecordType = RelatedRecordTypes.SalesInvoice,
            RelatedRecordId = invoice.Id(), CreatedAt = DateTimeOffset.UtcNow, PhoneNumber = "999900000002", DeliveryStatusId = StatusIds.MessageDelivery.Sending,
            DeliveryStatusAt = DateTimeOffset.UtcNow,
        };
        await api.UseDbAsync(db => db.Notifications.Add(row));

        var blocked = await (await sale.Manager.Client.ResendAsync(invoice.Id())).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal(("MSG-GEN-09", $"Sales invoice {invoice.Text("code")} is Sending and cannot be used for this action."), (blocked.Text("code"), blocked.Text("detail")));
        Assert.Equal(2, (await api.InvoiceSendingsAsync(invoice.Id())).Count);
        var view = await (await sale.Manager.Client.GetAsync($"/api/sales-invoices/{invoice.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Sending", view.Text("deliveryStatus"));

        var stale = DateTimeOffset.UtcNow - TraderMessageService.StaleSendingAfter - TimeSpan.FromMinutes(1);
        await api.UpdateAsync<Notification>(row.Id, n => (n.CreatedAt, n.DeliveryStatusAt) = (stale.AddSeconds(30), stale));
        await api.UpdateAsync<Notification>(firstId, n => n.CreatedAt = stale);
        Assert.Equal("Not sent", (await (await sale.Manager.Client.GetAsync($"/api/sales-invoices/{invoice.Id()}")).ReadAsync(HttpStatusCode.OK)).Text("deliveryStatus"));
        Assert.Equal("Sent", (await (await sale.Manager.Client.ResendAsync(invoice.Id())).ReadAsync(HttpStatusCode.OK)).Text("deliveryStatus"));
    }

    // Another message to the trader's phone about the invoice (the SALE-08 cancellation notice of PR 10) is not one of its sendings: neither the latest status nor the guard.
    [Fact]
    public async Task Another_phone_message_about_the_invoice_is_not_one_of_its_sendings()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: false);
        await api.UseDbAsync(db => db.Notifications.Add(new Notification
        {
            Channel = NotificationChannel.Sms, Type = "SALES_INVOICE_CANCELLED", Title = "Sales invoice cancelled", RelatedRecordType = RelatedRecordTypes.SalesInvoice,
            RelatedRecordId = invoice.Id(), CreatedAt = DateTimeOffset.UtcNow, PhoneNumber = "999900000002", DeliveryStatusId = StatusIds.MessageDelivery.Sending,
            DeliveryStatusAt = DateTimeOffset.UtcNow,
        }));

        var view = await (await sale.Manager.Client.GetAsync($"/api/sales-invoices/{invoice.Id()}")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Sent", 1), (view.Text("deliveryStatus"), view.GetProperty("sendings").GetArrayLength()));
        Assert.Equal("Sent", (await (await sale.Manager.Client.ResendAsync(invoice.Id())).ReadAsync(HttpStatusCode.OK)).Text("deliveryStatus"));
    }

    // BR-SALE-08, BR-SALE-09, §I.4.4: the Zone Manager of the zone and the Owner; another zone and the Accountant get MSG-GEN-08 and nothing is sent.
    [Fact]
    public async Task Only_the_zone_manager_of_the_zone_and_the_owner_finish_or_resend_and_a_cancelled_invoice_is_not_sent()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var otherZone = (await sale.Owner.Client.CreateZoneAsync()).Id();
        var outsider = await api.StaffAsync(await api.ZoneEntityAsync(otherZone));
        var accountant = await api.StaffAsync(null, StaffRole.Accountant);

        foreach (var client in new[] { outsider.Client, accountant.Client })
        {
            Assert.Equal("MSG-GEN-08", await (await client.FinishAsync(invoice.Id(), false)).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.ResendAsync(invoice.Id())).ErrorCodeAsync());
        }

        Assert.Empty(await api.InvoiceSendingsAsync(invoice.Id()));
        Assert.Null(await api.QueryAsync(db => db.SalesInvoices.Where(i => i.Id == invoice.Id()).Select(i => i.QrDecidedAt).SingleAsync()));
        Assert.Equal(HttpStatusCode.NotFound, (await sale.Owner.Client.ResendAsync(Guid.NewGuid())).StatusCode);

        // BR-SALE-08: the QR code is decided at the end of the sale, so nothing is sent before it; finish itself stays open (PM question).
        var rowVersion = await api.QueryAsync(db => db.SalesInvoices.Where(i => i.Id == invoice.Id()).Select(i => i.RowVersion).SingleAsync());
        var early = await (await sale.Owner.Client.ResendAsync(invoice.Id())).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-09", $"Sales invoice {invoice.Text("code")} is not finished and cannot be used for this action."), (early.Text("code"), early.Text("detail")));
        Assert.Empty(await api.InvoiceSendingsAsync(invoice.Id()));
        Assert.Empty(api.SmsTo(invoice.GetProperty("customer").Text("phoneNumber")!));
        Assert.Equal(rowVersion, await api.QueryAsync(db => db.SalesInvoices.Where(i => i.Id == invoice.Id()).Select(i => i.RowVersion).SingleAsync()));
        await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: false);
        Assert.Equal(HttpStatusCode.OK, (await sale.Owner.Client.ResendAsync(invoice.Id())).StatusCode);

        // PRE-1: a Cancelled invoice (SALE-08, PR 10) is never sent again.
        await api.UpdateAsync<SalesInvoice>(invoice.Id(), i => i.StatusId = StatusIds.SalesInvoice.Cancelled);
        var cancelled = await (await sale.Owner.Client.ResendAsync(invoice.Id())).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-09", $"Sales invoice {invoice.Text("code")} is Cancelled and cannot be used for this action."), (cancelled.Text("code"), cancelled.Text("detail")));
        Assert.Equal(2, (await api.InvoiceSendingsAsync(invoice.Id())).Count);
    }

    private const System.Text.Json.JsonValueKind JsonNull = System.Text.Json.JsonValueKind.Null;
}
