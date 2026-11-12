using System.Net;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-08: the lots this invoice closed, cash given back, the profile of a walk-in buyer, the notice to the trader and money owed back.</summary>
public class SalesInvoiceCancellationTests
{
    private static async Task<List<string>> ReadActionsAsync(HttpClient client, Guid id) => (await client.InvoiceAsync(id)).Actions();

    [Fact]
    public async Task A_cancelled_invoice_keeps_its_date_and_reason_counts_nowhere_and_tells_the_trader()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        await owner.FinishedAsync(sold.Id(), wantsQrCode: false);
        var current = await owner.InvoiceAsync(sold.Id());

        var answer = await owner.CancelledAsync(sold.Id(), Cancellation(current));

        var invoice = answer.GetProperty("invoice");
        Assert.Equal(("Cancelled", "The trader never took the goods"), (invoice.Text("status"), invoice.Text("cancelReason")));
        Assert.NotNull(invoice.Text("cancelledAt"));
        Assert.Equal(0m, await owner.CustomerDebtAsync(sale.CustomerId));
        Assert.Equal(["Print"], await ReadActionsAsync(owner, sold.Id()));
        Assert.Single(await api.InvoiceAuditAsync(sold.Id(), SalesInvoiceCancellationService.CancelledAction));

        var notice = api.SmsTo(await api.PhoneOfAsync(sale.CustomerId))[0];
        Assert.Contains($"invoice {sold.Text("code")}", notice.Text);
        Assert.Contains("has been cancelled", notice.Text);
        Assert.DoesNotContain("pay back", notice.Text);
        Assert.Equal(StatusIds.MessageDelivery.Sent, Assert.Single(await api.CancellationNoticesAsync(sold.Id())).DeliveryStatusId);
        Assert.Equal("Sent", answer.Text("deliveryStatus"));

        var second = await (await owner.CancelInvoiceAsync(sold.Id(), Cancellation(invoice))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", second.Text("code"));
    }

    [Fact]
    public async Task The_reason_is_required_and_only_the_owner_cancels()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);

        Assert.Equal(["Reason"], await (await sale.Owner.Client.CancelInvoiceAsync(sold.Id(), Cancellation(sold).With("reason", " "))).InvalidFieldsAsync());
        Assert.Equal("MSG-GEN-08", (await (await sale.Manager.Client.CancelInvoiceAsync(sold.Id(), Cancellation(sold))).ReadAsync(HttpStatusCode.Forbidden)).Text("code"));
        Assert.Equal(300_000m, await sale.Owner.Client.CustomerDebtAsync(sale.CustomerId));
    }

    // BR-HARVEST-04: only the lots this invoice closed reopen; a lot closed by another invoice, or never by this one, stays as it is.
    [Fact]
    public async Task Only_the_lots_this_invoice_closed_are_reopened()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var mine = await sale.SoldOnDebtAsync(sale.OtherLotId, 10m, ListPrice, notSoldWhole: false);
        var earlier = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        var closer = await sale.SoldOnDebtAsync(sale.LotId, 5m, ListPrice, notSoldWhole: false);

        await owner.CancelledAsync(mine.Id(), Cancellation(mine));
        await owner.CancelledAsync(earlier.Id(), Cancellation(earlier));

        var reopened = await api.LotOfAsync(sale.OtherLotId);
        Assert.Equal((StatusIds.HarvestLot.Reopened, (Guid?)null), (reopened.StatusId, reopened.ClosedBySalesInvoiceId));
        var stillClosed = await api.LotOfAsync(sale.LotId);
        Assert.Equal((StatusIds.HarvestLot.Closed, (Guid?)closer.Id()), (stillClosed.StatusId, stillClosed.ClosedBySalesInvoiceId));
    }

    // SALE-08.3: each cash payment Awaiting confirmation is asked about; Yes cancels it with the reason.
    [Fact]
    public async Task Cash_awaiting_confirmation_is_asked_about_and_cancelled_when_given_back()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var payment = Assert.Single(await api.PaymentsOfAsync(sold.Id()));

        var (code, field, body) = await (await owner.CancelInvoiceAsync(sold.Id(), Cancellation(sold))).QuestionAsync();
        Assert.Equal(("MSG-SALE-23", "cashGivenBack", payment.Id), (code, field, body.GetProperty("paymentId").GetGuid()));

        Assert.Equal(["Cash given back"], await (await owner.CancelInvoiceAsync(sold.Id(), Cancellation(sold).With("cashGivenBack", GivenBack(Guid.NewGuid(), true)))).InvalidFieldsAsync());

        await owner.CancelledAsync(sold.Id(), Cancellation(sold).With("cashGivenBack", GivenBack(payment.Id, true)));

        var cancelled = Assert.Single(await api.PaymentsOfAsync(sold.Id()));
        Assert.Equal((StatusIds.CustomerPayment.Cancelled, "The trader never took the goods"), (cancelled.StatusId, cancelled.CancelReason));
        Assert.Equal(0m, await owner.CustomerDebtAsync(sale.CustomerId));
    }

    // SALE-08.3 and BR-SALE-11: a No keeps the payment, so the money is owed back: warned, confirmed, notified; the trader hears it.
    [Fact]
    public async Task Cash_not_given_back_is_kept_and_leaves_money_owed_back()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var payment = Assert.Single(await api.PaymentsOfAsync(sold.Id()));
        var body = Cancellation(sold).With("cashGivenBack", GivenBack(payment.Id, false));

        var question = await (await owner.CancelInvoiceAsync(sold.Id(), body)).QuestionAsync();
        Assert.Equal(("MSG-SALE-17", "confirmOwedBack", 300_000m), (question.Code, question.Field, question.Body.Money("owedBackAmount")));
        Assert.Equal(StatusIds.SalesInvoice.Completed, (await owner.InvoiceAsync(sold.Id())).GetProperty("statusId").GetInt32());

        await owner.CancelledAsync(sold.Id(), body.With("confirmOwedBack", true));

        Assert.Equal(StatusIds.CustomerPayment.AwaitingConfirmation, Assert.Single(await api.PaymentsOfAsync(sold.Id())).StatusId);
        Assert.Equal(-300_000m, await owner.CustomerDebtAsync(sale.CustomerId));
        Assert.Single(await api.OwedBackNoticesAsync(sold.Id()));
        Assert.Contains("We will pay back the 300,000 VND you paid", api.SmsTo(await api.PhoneOfAsync(sale.CustomerId))[0].Text);
    }

    // BR-SALE-11: a Confirmed payment is never cancelled by the cancellation and asks nothing.
    [Fact]
    public async Task A_confirmed_payment_is_kept_without_a_question()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var payment = Assert.Single(await api.PaymentsOfAsync(sold.Id()));
        await api.UpdateAsync<CustomerPayment>(payment.Id, p => p.StatusId = StatusIds.CustomerPayment.Completed);

        await owner.CancelledAsync(sold.Id(), Cancellation(sold).With("confirmOwedBack", true));

        Assert.Equal(StatusIds.CustomerPayment.Completed, Assert.Single(await api.PaymentsOfAsync(sold.Id())).StatusId);
        Assert.Equal(-300_000m, await owner.CustomerDebtAsync(sale.CustomerId));
    }

    // SALE-08.1 and SALE-08.2: a walk-in invoice with kept payments first gets a profile; nothing kept, none is needed and nothing is sent.
    [Fact]
    public async Task A_walk_in_invoice_with_payments_kept_needs_a_profile_and_gets_the_notice_there()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.Manager.Client.SoldAsync(sale, sale.LotId, walkIn: true);
        var payment = Assert.Single(await api.PaymentsOfAsync(sold.Id()));
        var body = Cancellation(sold).With("cashGivenBack", GivenBack(payment.Id, false));

        var question = await (await owner.CancelInvoiceAsync(sold.Id(), body)).QuestionAsync();
        Assert.Equal(("MSG-SALE-19", "customerId"), (question.Code, question.Field));

        await owner.CancelledAsync(sold.Id(), body.With("customerId", sale.CustomerId).With("confirmOwedBack", true));

        Assert.Equal(-300_000m, await owner.CustomerDebtAsync(sale.CustomerId));
        Assert.Equal((Guid?)sale.CustomerId, Assert.Single(await api.PaymentsOfAsync(sold.Id())).CustomerId);
        Assert.Single(api.SmsTo(await api.PhoneOfAsync(sale.CustomerId)));

        var given = await sale.Manager.Client.SoldAsync(sale, sale.OtherLotId, walkIn: true);
        var paymentGiven = Assert.Single(await api.PaymentsOfAsync(given.Id()));
        var noProfile = await owner.CancelledAsync(given.Id(), Cancellation(given).With("cashGivenBack", GivenBack(paymentGiven.Id, true)));
        Assert.Equal("Not sent", noProfile.Text("deliveryStatus"));
        Assert.Empty(await api.CancellationNoticesAsync(given.Id()));
    }

    // GFR-06: the cancellation is saved whatever the SMS Service answers; the notice stays Not sent.
    [Fact]
    public async Task A_notice_the_service_refuses_keeps_the_cancellation()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        api.RefuseSmsTo(await api.PhoneOfAsync(sale.CustomerId));

        var answer = await sale.Owner.Client.CancelledAsync(sold.Id(), Cancellation(sold));

        Assert.Equal(("Not sent", "Cancelled"), (answer.Text("deliveryStatus"), answer.GetProperty("invoice").Text("status")));
        Assert.Equal(StatusIds.MessageDelivery.NotSent, Assert.Single(await api.CancellationNoticesAsync(sold.Id())).DeliveryStatusId);
    }

    // The statement lists a cancelled invoice on its date, counting 0 (BR-DEBT-04), and shows a price cut under its invoice.
    [Fact]
    public async Task The_statement_shows_corrections_and_a_cancellation_under_each_invoice()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var corrected = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        var cancelled = await sale.SoldOnDebtAsync(sale.OtherLotId, 10m, ListPrice);
        await owner.CorrectedAsync(corrected.Id(), PriceCut(corrected, 10_000m));
        await owner.CancelledAsync(cancelled.Id(), Cancellation(cancelled));

        var statement = await (await owner.GetAsync($"/api/customer-debts/{sale.CustomerId}/statement")).ReadAsync(HttpStatusCode.OK);

        var entries = statement.GetProperty("entries").EnumerateArray().ToList();
        var ofCorrected = entries.Single(e => e.Id() == corrected.Id());
        // The invoice keeps what it was saved at; the price cut is its own entry below it.
        Assert.Equal((300_000m, "PriceCutAfterClaim"), (ofCorrected.Money("debtChange"), ofCorrected.GetProperty("corrections")[0].Text("kind")));
        Assert.Equal(-10_000m, entries.Single(e => e.Text("kind") == "PriceCut" && e.Text("invoiceCode") == ofCorrected.Text("code")).Money("debtChange"));
        var ofCancelled = entries.Single(e => e.Id() == cancelled.Id());
        Assert.Equal((0m, true, "The trader never took the goods"), (ofCancelled.Money("debtChange"), ofCancelled.GetProperty("cancelled").GetBoolean(), ofCancelled.Text("cancelReason")));
        Assert.NotNull(ofCancelled.Text("cancelledAt"));
    }
}
