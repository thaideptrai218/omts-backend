using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-09.1: the lines, payments, sendings, price cuts and debt effect of one invoice, and the commands each role and state is offered.</summary>
public class SalesInvoiceDetailTests
{
    // SALE-09.1 step 2.
    [Fact]
    public async Task The_detail_shows_lines_payments_sendings_price_cuts_and_the_debt_effect()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = sale.Manager.Client;
        var sold = (await client.SellAsync(sale.PayLater(sale.LotId, 10m, cash: 100_000m))).GetProperty("invoice");
        await client.FinishedAsync(sold.Id(), wantsQrCode: false);
        await api.UseDbAsync(db => db.Set<SalesInvoicePriceCut>().Add(new SalesInvoicePriceCut
        {
            InvoiceId = sold.Id(), Amount = 50_000m, CutDate = PurchaseRequestTestKit.Today, Reason = "After a claim", EnteredByAccountId = sale.Owner.Account.Id,
        }));

        var invoice = await client.InvoiceAsync(sold.Id());

        var line = invoice.GetProperty("lines")[0];
        Assert.Equal((sale.LotId, 10m, ListPrice, "PriceList"), (line.GetProperty("harvestLotId").GetGuid(), line.Money("kg"), line.Money("unitPrice"), line.Text("priceSource")));
        var payment = invoice.GetProperty("payments")[0];
        Assert.Equal(("Cash", 100_000m), (payment.Text("method"), payment.Money("amount")));
        Assert.Single(invoice.GetProperty("sendings").EnumerateArray());
        var cut = invoice.GetProperty("priceCuts")[0];
        Assert.Equal((50_000m, "After a claim"), (cut.Money("amount"), cut.Text("reason")));

        var effect = invoice.GetProperty("debtEffect");
        Assert.Equal((false, 100_000m, 200_000m), (effect.GetProperty("isPaid").GetBoolean(), effect.Money("covered"), effect.Money("unpaid")));
        Assert.Equal((true, 200_000m), (effect.GetProperty("countsInCustomerDebt").GetBoolean(), effect.Money("customerDebt")));
    }

    [Fact]
    public async Task A_walk_in_invoice_counts_in_no_debt_and_a_cancelled_invoice_has_no_payment_state()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = sale.Manager.Client;
        var walkIn = await client.SoldAsync(sale, sale.LotId, walkIn: true);
        var cancelled = await client.SoldAsync(sale, sale.LotId);
        await api.UpdateAsync<SalesInvoice>(cancelled.Id(), i => i.StatusId = StatusIds.SalesInvoice.Cancelled);

        var ofWalkIn = (await client.InvoiceAsync(walkIn.Id())).GetProperty("debtEffect");
        Assert.Equal((true, false, JsonValueKind.Null), (ofWalkIn.GetProperty("isPaid").GetBoolean(), ofWalkIn.GetProperty("countsInCustomerDebt").GetBoolean(), ofWalkIn.GetProperty("customerDebt").ValueKind));

        var ofCancelled = (await client.InvoiceAsync(cancelled.Id())).GetProperty("debtEffect");
        Assert.Equal((JsonValueKind.Null, 0m, false), (ofCancelled.GetProperty("isPaid").ValueKind, ofCancelled.Money("unpaid"), ofCancelled.GetProperty("countsInCustomerDebt").GetBoolean()));
    }

    // SALE-09.1 step 3, by role, on a finished Completed invoice.
    [Fact]
    public async Task Each_role_is_offered_only_its_commands()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: false);
        var zone = await api.ZoneEntityAsync(sale.ZoneId);
        var accountant = await api.StaffAsync(zone, StaffRole.Accountant);
        var managerAll = await api.StaffAsync(null, StaffRole.ZoneManager);

        Assert.Equal(["Resend", "CreateReturn", "Correct", "Cancel", "Print"], (await sale.Owner.Client.InvoiceAsync(invoice.Id())).Actions());
        Assert.Equal(["Resend", "CreateReturn", "Print"], (await sale.Manager.Client.InvoiceAsync(invoice.Id())).Actions());
        Assert.Equal(["Resend", "CreateReturn", "Print"], (await managerAll.Client.InvoiceAsync(invoice.Id())).Actions());
        Assert.Empty((await accountant.Client.InvoiceAsync(invoice.Id())).Actions());
    }

    // A person may hold different roles in different zones (BR-FARM-08): the commands follow the pair for the zone of the invoice.
    [Fact]
    public async Task The_commands_follow_the_pair_of_the_invoice_zone_when_a_person_holds_several_pairs()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        var inA = await a.Manager.Client.SoldAsync(a, a.LotId);
        var inB = await b.Manager.Client.SoldAsync(b, b.LotId);
        await a.Manager.Client.FinishedAsync(inA.Id(), wantsQrCode: false);
        await b.Manager.Client.FinishedAsync(inB.Id(), wantsQrCode: false);
        var zoneA = await api.ZoneEntityAsync(a.ZoneId);
        var (account, _) = await api.StaffAccountAsync(zoneA, StaffRole.ZoneManager);
        await api.GrantAsync(account, StaffRole.Accountant, null);
        var client = await api.LoginAsync(account.Username);

        Assert.Equal(["Resend", "CreateReturn", "Print"], (await client.InvoiceAsync(inA.Id())).Actions());
        Assert.Empty((await client.InvoiceAsync(inB.Id())).Actions());
    }

    [Fact]
    public async Task An_unfinished_sale_is_not_offered_resend_and_a_cancelled_invoice_is_only_offered_print()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = sale.Manager.Client;
        var unfinished = await client.SoldAsync(sale, sale.LotId);
        var cancelled = await client.SoldAsync(sale, sale.LotId);
        await client.FinishedAsync(cancelled.Id(), wantsQrCode: false);
        await api.UpdateAsync<SalesInvoice>(cancelled.Id(), i => i.StatusId = StatusIds.SalesInvoice.Cancelled);

        Assert.Equal(["CreateReturn", "Print"], (await client.InvoiceAsync(unfinished.Id())).Actions());
        Assert.Equal(["Print"], (await client.InvoiceAsync(cancelled.Id())).Actions());
        Assert.Equal(["Print"], (await sale.Owner.Client.InvoiceAsync(cancelled.Id())).Actions());
    }

    // BR-SALE-09: not while the latest sending is Sending; a Sending row left by a server stop reads Not sent.
    [Fact]
    public async Task Resend_is_not_offered_while_the_latest_sending_is_sending_and_is_offered_when_that_sending_is_stale()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = sale.Manager.Client;
        var invoice = await client.SoldAsync(sale, sale.LotId);
        await client.FinishedAsync(invoice.Id(), wantsQrCode: false);
        var row = new Notification
        {
            Channel = NotificationChannel.Sms, Type = SalesInvoiceMessages.MessageType, Title = SalesInvoiceMessages.Title, RelatedRecordType = RelatedRecordTypes.SalesInvoice,
            RelatedRecordId = invoice.Id(), CreatedAt = DateTimeOffset.UtcNow, PhoneNumber = "999900000002", DeliveryStatusId = StatusIds.MessageDelivery.Sending,
            DeliveryStatusAt = DateTimeOffset.UtcNow,
        };
        await api.UseDbAsync(db => db.Notifications.Add(row));

        var sending = await client.InvoiceAsync(invoice.Id());
        Assert.Equal(["CreateReturn", "Print"], sending.Actions());
        Assert.Equal(StatusIds.MessageDelivery.Sending, sending.GetProperty("deliveryStatusId").GetInt32());

        await api.UpdateAsync<Notification>(row.Id, n => n.DeliveryStatusAt = DateTimeOffset.UtcNow.AddMinutes(-3));

        Assert.Equal(["Resend", "CreateReturn", "Print"], (await client.InvoiceAsync(invoice.Id())).Actions());
    }

    // A command that is offered works, and one that is not answers no 500: Resend follows the same rule as its endpoint.
    [Fact]
    public async Task A_command_offered_is_accepted_by_its_endpoint()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = sale.Manager.Client;
        var invoice = await client.SoldAsync(sale, sale.LotId);

        Assert.DoesNotContain("Resend", (await client.InvoiceAsync(invoice.Id())).Actions());
        Assert.Equal(HttpStatusCode.Conflict, (await client.ResendAsync(invoice.Id())).StatusCode);

        await client.FinishedAsync(invoice.Id(), wantsQrCode: false);

        Assert.Contains("Resend", (await client.InvoiceAsync(invoice.Id())).Actions());
        Assert.Equal(HttpStatusCode.OK, (await client.ResendAsync(invoice.Id())).StatusCode);
    }
}
