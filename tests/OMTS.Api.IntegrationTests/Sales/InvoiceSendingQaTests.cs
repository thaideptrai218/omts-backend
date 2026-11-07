using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of PR 6b (OMTS-224): the end of the sale and SALE-06 against the SRS 27/10.</summary>
public class InvoiceSendingQaTests
{
    // BR-ORIGIN-01, MSG-SALE-15, AC-12: the QR code and the SMS carry the link of the public provenance page. appsettings.json ships "Provenance:PublicBaseUrl" empty and nothing
    // refuses it at start, so every environment that forgets it prints QR codes and sends SMS with "/origin/{token}", which no trader can open. Either the host refuses to start
    // or the link is an absolute http(s) URL.
    [Fact]
    public async Task An_empty_public_base_url_never_gives_a_relative_origin_link_to_the_qr_code_or_the_sms()
    {
        using var api = new EmptyProvenanceBaseApi();
        SaleSetup sale;
        try
        {
            sale = await api.SaleSetupAsync();
        }
        catch (Exception exception) when (exception.GetBaseException() is OptionsValidationException)
        {
            return;
        }

        await api.PublishAsync(sale.LotId);
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);

        var finished = await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: true);

        var qrUrl = finished.Text("qrUrl")!;
        Assert.True(IsWebLink(qrUrl), $"The QR code links to '{qrUrl}', not to an absolute http(s) URL.");
        var sms = Assert.Single(api.SmsTo(invoice.GetProperty("customer").Text("phoneNumber")!));
        var link = sms.Text[(sms.Text.IndexOf(" Origin: ", StringComparison.Ordinal) + " Origin: ".Length)..];
        Assert.True(IsWebLink(link), $"MSG-SALE-15 carries the origin link '{link}', not an absolute http(s) URL.");
    }

    // SALE-06.0.E1 step 1 and POST-1, BR-SALE-09: when nothing can be sent, the invoice shows Not sent, also when nothing could be sent. Here the first sending was Sent, then
    // the profile lost its phone number (a registration profile, or SALE-07.5 later); Resend answers Not sent + MSG-SALE-16, but writes no sending, so the invoice keeps showing
    // the older Sent of a message that no longer matches what the Zone Manager just saw.
    [Fact]
    public async Task A_resend_that_sends_nothing_shows_not_sent_on_the_invoice_even_after_an_earlier_sent()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        Assert.Equal("Sent", (await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: false)).Text("deliveryStatus"));
        await api.UpdateAsync<Customer>(sale.CustomerId, c => c.PhoneNumber = null);

        var resent = await (await sale.Manager.Client.ResendAsync(invoice.Id())).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Not sent", "MSG-SALE-16"), (resent.Text("deliveryStatus"), resent.Text("messageCode")));
        Assert.Equal("Not sent", resent.GetProperty("invoice").Text("deliveryStatus"));
        var read = await (await sale.Owner.Client.GetAsync($"/api/sales-invoices/{invoice.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Not sent", read.Text("deliveryStatus"));
    }

    // Guard (passes today). RecipientAccountId is now nullable: the sendings of an invoice, with the trader account or with none, never reach a notification list (BR-SALE-09,
    // BR-ACCOUNT-11, ACCOUNT-08): not in the trader's list or unread count, not openable by id, not in the Owner's list; a trader can neither finish nor resend (§I.4.4 SALE-06: No).
    [Fact]
    public async Task Invoice_sendings_with_or_without_a_recipient_never_reach_a_notification_list_and_a_trader_cannot_send()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var trader = await api.TraderAsync();
        var traded = await sale.Manager.Client.SoldAsync(sale, sale.LotId, customerId: trader.Profile.Id);
        var noAccount = await sale.Manager.Client.SoldAsync(sale, sale.OtherLotId);

        Assert.Equal(HttpStatusCode.Forbidden, (await trader.Client.FinishAsync(traded.Id(), false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.Client.ResendAsync(traded.Id())).StatusCode);
        Assert.Empty(await api.InvoiceSendingsAsync(traded.Id()));

        await sale.Manager.Client.FinishedAsync(traded.Id(), wantsQrCode: false);
        (await sale.Manager.Client.ResendAsync(traded.Id())).EnsureSuccessStatusCode();
        await sale.Manager.Client.FinishedAsync(noAccount.Id(), wantsQrCode: false);

        var rows = (await api.InvoiceSendingsAsync(traded.Id())).Concat(await api.InvoiceSendingsAsync(noAccount.Id())).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal([trader.Account.Id, trader.Account.Id, null], rows.Select(r => r.RecipientAccountId).ToList());
        var traderList = await (await trader.Client.GetAsync("/api/me/notifications")).ReadAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(traderList.GetProperty("items").EnumerateArray(), n => n.Text("type") == SalesInvoiceMessages.MessageType);
        Assert.Equal(0, traderList.GetProperty("unreadCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await trader.Client.PostAsync($"/api/me/notifications/{rows[0].Id}/read", null)).StatusCode);
        var ownerList = await (await sale.Owner.Client.GetAsync("/api/me/notifications?pageSize=100")).ReadAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(ownerList.GetProperty("items").EnumerateArray(), n => n.Text("type") == SalesInvoiceMessages.MessageType);
        var ownerRows = await api.QueryAsync(db => db.Notifications.CountAsync(n => n.RecipientAccountId == sale.Owner.Account.Id && n.Channel == NotificationChannel.InApp));
        Assert.Equal(ownerRows, ownerList.GetProperty("total").GetInt32());
    }

    private static bool IsWebLink(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.Host.Length > 0;

    /// <summary>The API as deployed with the appsettings.json default: no public base URL of the web app.</summary>
    private sealed class EmptyProvenanceBaseApi : AuthApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Provenance:PublicBaseUrl", string.Empty);
        }
    }
}
