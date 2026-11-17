using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSearchTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>Independent QA of the paid state (BR-DEBT-05), the debt effect and price cuts, the cash filter, merged profiles and the commands of SALE-09.1.</summary>
public class SalesInvoiceDetailQaTests
{
    private static readonly DateOnly Today = PurchaseRequestTestKit.Today;

    private static string Span => Query(("from", Today.AddDays(-5)), ("to", Today), ("pageSize", 100));

    private static async Task<Guid> NewCustomerAsync(SaleSetup sale) => (await sale.Owner.Client.CreateCustomerAsync()).Id();

    private static async Task<(bool? IsPaid, decimal Unpaid, decimal Covered)> StateAsync(HttpClient client, JsonElement list, JsonElement invoice)
    {
        var row = list.Rows().Single(r => r.Id() == invoice.Id());
        var effect = (await client.InvoiceAsync(invoice.Id())).GetProperty("debtEffect");
        bool? isPaid = row.GetProperty("isPaid").ValueKind == JsonValueKind.Null ? null : row.GetProperty("isPaid").GetBoolean();
        Assert.Equal(row.GetProperty("isPaid").ValueKind, effect.GetProperty("isPaid").ValueKind);
        Assert.Equal(row.Money("unpaidAmount"), effect.Money("unpaid"));
        return (isPaid, row.Money("unpaidAmount"), effect.Money("covered"));
    }

    // BR-DEBT-05 and BR-DEBT-02 by case: the list and the detail give one answer.
    [Fact]
    public async Task Paid_follows_the_allocation_for_each_way_of_paying()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var manager = sale.Manager.Client;
        var cash = await manager.SoldAsync(sale, sale.LotId);
        var partCash = (await manager.SellAsync(sale.PayLater(sale.LotId, 10m, cash: 100_000m, customerId: await NewCustomerAsync(sale)))).GetProperty("invoice");
        var payLater = (await manager.SellAsync(sale.PayLater(sale.LotId, 10m, customerId: await NewCustomerAsync(sale)))).GetProperty("invoice");
        var awaitingCustomer = await NewCustomerAsync(sale);
        var awaitingCovered = (await manager.SellAsync(sale.PayLater(sale.LotId, 10m, customerId: awaitingCustomer))).GetProperty("invoice");
        await api.RecordPaymentAsync(awaitingCustomer, 300_000m, statusId: StatusIds.CustomerPayment.AwaitingConfirmation);
        var unconfirmedCash = await NewCustomerAsync(sale);
        var cashCancelled = await manager.SoldAsync(sale, sale.LotId, customerId: unconfirmedCash);
        var walkIn = await manager.SoldAsync(sale, sale.LotId, walkIn: true);
        var walkInCancelled = await manager.SoldAsync(sale, sale.LotId, walkIn: true);
        foreach (var payment in (await api.PaymentsOfAsync(cashCancelled.Id())).Concat(await api.PaymentsOfAsync(walkInCancelled.Id())))
        {
            await api.UpdateAsync<CustomerPayment>(payment.Id, p => p.StatusId = StatusIds.CustomerPayment.Cancelled);
        }

        var list = await manager.InvoiceListAsync(Span);

        Assert.Equal((true, 0m, 300_000m), await StateAsync(manager, list, cash));
        Assert.Equal((false, 200_000m, 100_000m), await StateAsync(manager, list, partCash));
        Assert.Equal((false, 300_000m, 0m), await StateAsync(manager, list, payLater));
        Assert.Equal((true, 0m, 300_000m), await StateAsync(manager, list, awaitingCovered));
        Assert.Equal((false, 300_000m, 0m), await StateAsync(manager, list, cashCancelled));
        Assert.Equal((true, 0m, 300_000m), await StateAsync(manager, list, walkIn));
        Assert.Equal((false, 300_000m, 0m), await StateAsync(manager, list, walkInCancelled));
    }

    // BR-DEBT-05 (3) and BR-SALE-11: the cash kept after a cancellation covers the oldest invoice of the customer.
    [Fact]
    public async Task The_cash_kept_on_a_cancelled_invoice_covers_the_oldest_invoice_and_a_cancelled_invoice_has_no_paid_state()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var manager = sale.Manager.Client;
        var customer = await NewCustomerAsync(sale);
        var older = (await manager.SellAsync(sale.PayLater(sale.LotId, 10m, customerId: customer))).GetProperty("invoice");
        await api.BackDateAsync(older.Id(), 2);
        var cancelled = await manager.SoldAsync(sale, sale.LotId, customerId: customer);
        await api.UpdateAsync<SalesInvoice>(cancelled.Id(), i => i.StatusId = StatusIds.SalesInvoice.Cancelled);

        var list = await manager.InvoiceListAsync(Span);

        Assert.Equal((true, 0m, 300_000m), await StateAsync(manager, list, older));
        Assert.Equal((null, 0m, 0m), await StateAsync(manager, list, cancelled));
        Assert.DoesNotContain(cancelled.Text("code"), (await manager.InvoiceListAsync(Span + "&paid=true")).Codes());
        Assert.DoesNotContain(cancelled.Text("code"), (await manager.InvoiceListAsync(Span + "&paid=false")).Codes());
        Assert.Equal(0m, await manager.CustomerDebtAsync(customer));
    }

    // BR-DEBT-02: a debt of zero or below leaves no invoice unpaid, money paid back included.
    [Fact]
    public async Task Money_owed_back_leaves_no_invoice_unpaid_and_paying_it_back_changes_nothing_on_the_invoice()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var manager = sale.Manager.Client;
        var customer = await NewCustomerAsync(sale);
        var invoice = (await manager.SellAsync(sale.PayLater(sale.LotId, 10m, customerId: customer))).GetProperty("invoice");
        await api.RecordPaymentAsync(customer, 400_000m);
        await api.RecordPaymentAsync(customer, 100_000m, kind: CustomerPaymentKind.PaidBack);

        var list = await manager.InvoiceListAsync(Span);

        Assert.Equal((true, 0m, 300_000m), await StateAsync(manager, list, invoice));
        Assert.Equal(0m, (await manager.InvoiceAsync(invoice.Id())).GetProperty("debtEffect").Money("customerDebt"));
    }

    // SALE-09.1 step 2 and BR-SALE-10 (3): the cuts oldest first, the latest total, the money the invoice no longer needs (BR-DEBT-05 (3)).
    [Fact]
    public async Task Price_cuts_lower_the_total_and_the_debt_effect_and_are_listed_oldest_first()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var customer = await NewCustomerAsync(sale);
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId, customerId: customer);
        await api.UpdateAsync<SalesInvoice>(invoice.Id(), i => (i.PriceCutTotal, i.Total) = (100_000m, 200_000m));
        await api.UseDbAsync(db =>
        {
            db.Set<SalesInvoicePriceCut>().Add(new SalesInvoicePriceCut { InvoiceId = invoice.Id(), Amount = 60_000m, CutDate = Today, Reason = "Second", EnteredByAccountId = sale.Owner.Account.Id });
            db.Set<SalesInvoicePriceCut>().Add(new SalesInvoicePriceCut { InvoiceId = invoice.Id(), Amount = 40_000m, CutDate = Today.AddDays(-1), Reason = "First", EnteredByAccountId = sale.Owner.Account.Id });
        });

        var view = await sale.Manager.Client.InvoiceAsync(invoice.Id());
        var row = (await sale.Manager.Client.InvoiceListAsync()).Rows().Single(r => r.Id() == invoice.Id());

        Assert.Equal(new[] { "First", "Second" }, view.GetProperty("priceCuts").EnumerateArray().Select(c => c.Text("reason")));
        Assert.Equal((200_000m, 100_000m), (view.Money("total"), view.Money("priceCutTotal")));
        var effect = view.GetProperty("debtEffect");
        Assert.Equal((true, 200_000m, 0m, -100_000m), (effect.GetProperty("isPaid").GetBoolean(), effect.Money("covered"), effect.Money("unpaid"), effect.Money("customerDebt")));
        Assert.Equal((200_000m, true), (row.Money("total"), row.GetProperty("isPaid").GetBoolean()));
    }

    // SALE-09.1 step 2: "its payments with their method, amount, time and status". The view has the date of the payment only.
    [Fact]
    public async Task The_payments_of_the_detail_carry_their_time()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);

        var payment = (await sale.Manager.Client.InvoiceAsync(invoice.Id())).GetProperty("payments")[0];

        var names = payment.EnumerateObject().Select(p => p.Name).ToList();
        Assert.True(new[] { "createdAt", "recordedAt", "paidAt", "paymentTime" }.Any(names.Contains), "The payment has no time: " + string.Join(", ", names));
    }

    // BR-SALE-09: the list reads the latest sending as the detail does, and a stale Sending as Not sent.
    [Fact]
    public async Task The_list_and_the_detail_agree_on_the_delivery_status_of_the_latest_sending()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = sale.Manager.Client;
        var invoice = await client.SoldAsync(sale, sale.LotId);
        await client.FinishedAsync(invoice.Id(), wantsQrCode: false);
        var latest = new Notification
        {
            Channel = NotificationChannel.Sms, Type = SalesInvoiceMessages.MessageType, Title = SalesInvoiceMessages.Title, RelatedRecordType = RelatedRecordTypes.SalesInvoice,
            RelatedRecordId = invoice.Id(), CreatedAt = DateTimeOffset.UtcNow.AddSeconds(1), PhoneNumber = "999900000002", DeliveryStatusId = StatusIds.MessageDelivery.Sending,
            DeliveryStatusAt = DateTimeOffset.UtcNow,
        };
        await api.UseDbAsync(db => db.Notifications.Add(latest));

        async Task<(int List, int Detail)> DeliveryAsync()
        {
            var row = (await client.InvoiceListAsync()).Rows().Single(r => r.Id() == invoice.Id());
            return (row.GetProperty("deliveryStatusId").GetInt32(), (await client.InvoiceAsync(invoice.Id())).GetProperty("deliveryStatusId").GetInt32());
        }

        Assert.Equal((StatusIds.MessageDelivery.Sending, StatusIds.MessageDelivery.Sending), await DeliveryAsync());
        await api.UpdateAsync<Notification>(latest.Id, n => n.DeliveryStatusAt = DateTimeOffset.UtcNow.AddMinutes(-3));
        Assert.Equal((StatusIds.MessageDelivery.NotSent, StatusIds.MessageDelivery.NotSent), await DeliveryAsync());
    }

    // SALE-09 step 3: the cash of this caller, Awaiting confirmation, and only that amount.
    [Fact]
    public async Task The_cash_filter_shows_the_amount_of_the_awaiting_cash_of_this_caller_only()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var manager = sale.Manager;
        var invoice = (await manager.Client.SellAsync(sale.PayLater(sale.LotId, 10m, cash: 100_000m))).GetProperty("invoice");
        var other = await api.ManagerAsync(sale.ZoneId);

        CustomerPayment Extra(int status, Guid collector, decimal amount) => new()
        {
            Code = "PT" + AcsTestKit.NewCode(), CustomerId = sale.CustomerId, InvoiceId = invoice.Id(), Kind = CustomerPaymentKind.Payment, Amount = amount, Method = CustomerPaymentMethod.Cash,
            PaymentDate = Today, StatusId = status, CollectedByAccountId = collector,
        };
        await api.UseDbAsync(db => db.CustomerPayments.AddRange(
            Extra(StatusIds.CustomerPayment.AwaitingConfirmation, manager.Account.Id, 10_000m),
            Extra(StatusIds.CustomerPayment.Cancelled, manager.Account.Id, 1_000m),
            Extra(StatusIds.CustomerPayment.Completed, manager.Account.Id, 2_000m),
            Extra(StatusIds.CustomerPayment.AwaitingConfirmation, other.Account.Id, 4_000m)));

        Assert.Equal(110_000m, (await manager.Client.InvoiceListAsync("?cashCollectedByMe=true")).Rows()[0].Money("cashAwaitingConfirmation"));
        Assert.Equal(4_000m, (await other.Client.InvoiceListAsync("?cashCollectedByMe=true")).Rows()[0].Money("cashAwaitingConfirmation"));
    }

    // BR-FARM-08: cash collected in a zone the caller has left is no longer shown.
    [Fact]
    public async Task The_cash_filter_stays_inside_the_zones_of_the_caller()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        var (account, pairA) = await api.StaffAccountAsync(await api.ZoneEntityAsync(a.ZoneId));
        await api.GrantAsync(account, StaffRole.ZoneManager, await api.ZoneEntityAsync(b.ZoneId));
        var client = await api.LoginAsync(account.Username);
        await client.SoldAsync(a, a.LotId);
        Assert.Single((await client.InvoiceListAsync("?cashCollectedByMe=true")).Rows());

        Assert.Equal(HttpStatusCode.OK, (await a.Owner.Client.PostAsJsonAsync($"/api/accounts/{account.Id}/grants/{pairA.Id}/revoke", new { reason = "Moved" })).StatusCode);

        Assert.Empty((await client.InvoiceListAsync("?cashCollectedByMe=true")).Rows());
    }

    // BR-SALE-04: the invoices of a merged profile are shown and counted with the kept one, and the merged profile can no longer be chosen.
    [Fact]
    public async Task After_a_merge_the_kept_profile_has_the_invoices_of_both_and_one_allocation()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var manager = sale.Manager.Client;
        var mergedId = await NewCustomerAsync(sale);
        var ofMerged = (await manager.SellAsync(sale.PayLater(sale.LotId, 10m, customerId: mergedId))).GetProperty("invoice");
        await api.BackDateAsync(ofMerged.Id(), 2);
        var ofKept = (await manager.SellAsync(sale.PayLater(sale.LotId, 10m))).GetProperty("invoice");
        Assert.Equal(HttpStatusCode.OK, (await sale.Owner.Client.MergeAsync(sale.CustomerId, mergedId, ownerApproved: true)).StatusCode);
        await api.RecordPaymentAsync(sale.CustomerId, 300_000m);

        var kept = await manager.InvoiceListAsync(Span + $"&customerId={sale.CustomerId}");
        var keptName = (await manager.CustomerAsync(sale.CustomerId)).Text("name");

        Assert.Equal(new[] { ofKept.Text("code"), ofMerged.Text("code") }.Order(), kept.Codes().Order());
        Assert.All(kept.Rows(), r => Assert.Equal((sale.CustomerId, keptName), (r.GetProperty("customer").Id(), r.GetProperty("customer").Text("name"))));
        Assert.Equal((true, 0m, 300_000m), await StateAsync(manager, kept, ofMerged));
        Assert.Equal((false, 300_000m, 0m), await StateAsync(manager, kept, ofKept));
        var byMergedId = await manager.InvoiceListAsync(Span + $"&customerId={mergedId}");
        Assert.Equal((0, "MSG-GEN-10"), (byMergedId.Rows().Count, byMergedId.Text("messageCode")));
    }

    // SALE-09.1 step 3: rights add up over two pairs of one zone (Accountant + Zone Manager); the commands of the answers follow the same rule.
    [Fact]
    public async Task A_person_with_two_roles_in_one_zone_gets_the_larger_set_and_the_save_answers_carry_the_callers_commands()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var zone = await api.ZoneEntityAsync(sale.ZoneId);
        var (account, _) = await api.StaffAccountAsync(zone);
        await api.GrantAsync(account, StaffRole.Accountant, zone);
        var both = await api.LoginAsync(account.Username);

        var created = (await both.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 1m)], Cash(ListPrice), sale.LotId))).GetProperty("invoice");
        Assert.Equal(["CreateReturn", "Print"], created.Actions());

        var finished = (await both.FinishedAsync(created.Id(), wantsQrCode: false)).GetProperty("invoice");
        Assert.Equal(["Resend", "CreateReturn", "Print"], finished.Actions());
        Assert.Equal(["Resend", "CreateReturn", "Print"], (await both.InvoiceAsync(created.Id())).Actions());
        Assert.Equal(["Resend", "CreateReturn", "Edit", "Correct", "Cancel", "Print"], (await sale.Owner.Client.InvoiceAsync(created.Id())).Actions());
    }

    // The test changed by the developer (Ac_12): beside the same caller, another caller reads the same invoice, the commands apart.
    [Fact]
    public async Task The_finish_answer_and_the_get_by_the_owner_differ_only_in_the_commands()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);

        var view = (await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: false)).GetProperty("invoice");
        var read = await sale.Owner.Client.InvoiceAsync(invoice.Id());

        Assert.Equal(WithoutCommands(view), WithoutCommands(read));
        Assert.NotEqual(view.Actions(), read.Actions());
    }

    private static string WithoutCommands(JsonElement view)
    {
        var node = JsonNode.Parse(view.GetRawText())!.AsObject();
        node.Remove("allowedActions");
        return node.ToJsonString();
    }
}
