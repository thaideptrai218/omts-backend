using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>Independent QA of SALE-08 (BR-SALE-11, BR-DEBT-03): cash given back, the lots, cancelling after corrections, who may do it, and what is sent to whom.</summary>
public class SalesInvoiceCancellationQaTests
{
    private static async Task<Guid> NewCustomerAsync(SaleSetup sale) => (await sale.Owner.Client.CreateCustomerAsync()).Id();

    /// <summary>A payment of the invoice that the Accountant recorded or the bank reported, inserted as the DEBT-02 and GFR-08 paths would leave it.</summary>
    private static async Task<CustomerPayment> AddInvoicePaymentAsync(AuthApiFactory api, Guid invoiceId, Guid? customerId, decimal amount, CustomerPaymentMethod method, int statusId)
    {
        var payment = new CustomerPayment
        {
            Code = "PT" + AcsTestKit.NewCode(), CustomerId = customerId, InvoiceId = invoiceId, Kind = CustomerPaymentKind.Payment, Amount = amount, Method = method,
            PaymentDate = PurchaseRequestTestKit.Today, StatusId = statusId,
        };
        await api.UseDbAsync(db => db.CustomerPayments.Add(payment));
        return payment;
    }

    // SALE-08.3: two cash payments are asked one at a time, each answered independently; only a Yes is cancelled; the debt keeps the No.
    [Fact]
    public async Task Each_cash_payment_is_asked_on_its_own_and_only_a_yes_is_cancelled()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var first = Assert.Single(await api.PaymentsOfAsync(sold.Id()));
        var second = await AddInvoicePaymentAsync(api, sold.Id(), sale.CustomerId, 40_000m, CustomerPaymentMethod.Cash, StatusIds.CustomerPayment.AwaitingConfirmation);

        var one = await (await owner.CancelInvoiceAsync(sold.Id(), Cancellation(sold).With("cashGivenBack", GivenBack(first.Id, true)))).QuestionAsync();
        Assert.Equal(("MSG-SALE-23", "cashGivenBack", second.Id), (one.Code, one.Field, one.Body.GetProperty("paymentId").GetGuid()));

        var both = new[] { new { paymentId = first.Id, givenBack = true }, new { paymentId = second.Id, givenBack = false } };
        var owed = await (await owner.CancelInvoiceAsync(sold.Id(), Cancellation(sold).With("cashGivenBack", both))).QuestionAsync();
        Assert.Equal(("MSG-SALE-17", 40_000m), (owed.Code, owed.Body.Money("owedBackAmount")));

        await owner.CancelledAsync(sold.Id(), Cancellation(sold).With("cashGivenBack", both).With("confirmOwedBack", true));

        var payments = await api.PaymentsOfAsync(sold.Id());
        Assert.Equal(StatusIds.CustomerPayment.Cancelled, payments.Single(p => p.Id == first.Id).StatusId);
        Assert.Equal(StatusIds.CustomerPayment.AwaitingConfirmation, payments.Single(p => p.Id == second.Id).StatusId);
        Assert.Equal(-40_000m, await owner.CustomerDebtAsync(sale.CustomerId));
        Assert.Contains("We will pay back the 40,000 VND you paid", api.SmsTo(await api.PhoneOfAsync(sale.CustomerId))[0].Text);
    }

    // A malformed list element must be a field error (MSG-GEN-01), not an exception.
    [Fact]
    public async Task A_null_answer_in_the_cash_list_answers_msg_gen_01()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.Manager.Client.SoldAsync(sale, sale.LotId);

        var answer = await owner.CancelInvoiceAsync(sold.Id(), Cancellation(sold).With("cashGivenBack", new object?[] { null }));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal("MSG-GEN-01", (await answer.ReadAsync(HttpStatusCode.BadRequest)).Text("code"));
    }

    // BR-SALE-11: a VietQR payment is never asked about and is kept, also while it is Awaiting confirmation; a payment already Cancelled is neither asked nor counted.
    [Fact]
    public async Task Vietqr_payments_are_kept_and_payments_already_cancelled_are_ignored()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        var transfer = await AddInvoicePaymentAsync(api, sold.Id(), sale.CustomerId, 100_000m, CustomerPaymentMethod.VietQrInvoice, StatusIds.CustomerPayment.AwaitingConfirmation);
        await AddInvoicePaymentAsync(api, sold.Id(), sale.CustomerId, 50_000m, CustomerPaymentMethod.Cash, StatusIds.CustomerPayment.Cancelled);
        var current = await owner.InvoiceAsync(sold.Id());

        var question = await (await owner.CancelInvoiceAsync(sold.Id(), Cancellation(current))).QuestionAsync();
        Assert.Equal(("MSG-SALE-17", "confirmOwedBack", 100_000m), (question.Code, question.Field, question.Body.Money("owedBackAmount")));

        await owner.CancelledAsync(sold.Id(), Cancellation(current).With("confirmOwedBack", true));

        var kept = (await api.PaymentsOfAsync(sold.Id())).Single(p => p.Id == transfer.Id);
        Assert.Equal(StatusIds.CustomerPayment.AwaitingConfirmation, kept.StatusId);
        Assert.Equal(-100_000m, await owner.CustomerDebtAsync(sale.CustomerId));
    }

    // BR-DEBT-02 and SALE-08 POST-2: cancelling a part-paid invoice among others; list, statement, customer view give one debt; the kept cash covers the oldest invoice.
    [Fact]
    public async Task A_cancellation_with_cash_kept_leaves_one_debt_everywhere_and_covers_the_oldest_invoice()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var customer = await NewCustomerAsync(sale);
        var older = await sale.PayLaterAsync(customer, 10m);
        await api.BackDateAsync(older.Id(), 2);
        var newer = await sale.PayLaterAsync(customer, 10m, cash: 100_000m);
        var payment = Assert.Single(await api.PaymentsOfAsync(newer.Id()));

        await owner.CancelledAsync(newer.Id(), Cancellation(newer).With("cashGivenBack", GivenBack(payment.Id, false)));

        Assert.Equal((200_000m, 200_000m, 200_000m), await owner.DebtsEverywhereAsync(customer));
        var statement = await owner.StatementAsync(customer);
        var (code, unpaid) = Assert.Single(statement.Unpaid());
        Assert.Equal((older.Text("code"), 200_000m), (code, unpaid));
        var cancelledEntry = statement.GetProperty("entries").EnumerateArray().Single(e => e.Id() == newer.Id());
        Assert.Equal((0m, true), (cancelledEntry.Money("debtChange"), cancelledEntry.GetProperty("cancelled").GetBoolean()));
    }

    // SALE-08: cancelling after several corrections takes the invoice out at its latest total, never at an older one.
    [Fact]
    public async Task Cancelling_after_corrections_takes_out_the_latest_total()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        var cut = (await owner.CorrectedAsync(sold.Id(), PriceCut(sold, 100_000m))).GetProperty("invoice");
        await sale.SoldOnDebtAsync(sale.OtherLotId, 1m, ListPrice);
        Assert.Equal(230_000m, await owner.CustomerDebtAsync(sale.CustomerId));

        var cancelled = (await owner.CancelledAsync(sold.Id(), Cancellation(cut))).GetProperty("invoice");

        Assert.Equal(30_000m, await owner.CustomerDebtAsync(sale.CustomerId));
        Assert.Equal(200_000m, cancelled.Money("total"));
        Assert.Equal(1, cancelled.GetProperty("priceCuts").GetArrayLength());
        Assert.Contains("has been cancelled", api.SmsTo(await api.PhoneOfAsync(sale.CustomerId))[0].Text);
    }

    // Cancelling twice, by the same body or a fresh read, changes nothing the second time.
    [Fact]
    public async Task Cancelling_twice_changes_nothing_the_second_time()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice, notSoldWhole: false);

        var first = (await owner.CancelledAsync(sold.Id(), Cancellation(sold))).GetProperty("invoice");
        var fresh = await owner.InvoiceAsync(sold.Id());
        var second = await (await owner.CancelInvoiceAsync(sold.Id(), Cancellation(fresh))).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal("MSG-GEN-09", second.Text("code"));
        Assert.Single(await api.InvoiceAuditAsync(sold.Id(), SalesInvoiceCancellationService.CancelledAction));
        Assert.Single(await api.CancellationNoticesAsync(sold.Id()));
        Assert.Equal(1, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == sale.LotId && e.Action == "HarvestLot.Reopened")));
        Assert.Single(api.SmsTo(await api.PhoneOfAsync(sale.CustomerId)));
        Assert.Equal(first.Text("cancelledAt"), (await owner.InvoiceAsync(sold.Id())).Text("cancelledAt"));
    }

    // BR-HARVEST-04: a lot shared with another invoice reopens only when the closer is cancelled; then it can be sold again; the kg no longer count.
    [Fact]
    public async Task A_shared_lot_reopens_only_with_its_closer_and_can_then_be_sold_again()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var plain = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        var closer = await sale.SoldOnDebtAsync(sale.LotId, 5m, ListPrice, notSoldWhole: false);
        Assert.Equal(StatusIds.HarvestLot.Closed, (await api.LotOfAsync(sale.LotId)).StatusId);

        await owner.CancelledAsync(plain.Id(), Cancellation(plain));
        Assert.Equal(StatusIds.HarvestLot.Closed, (await api.LotOfAsync(sale.LotId)).StatusId);
        Assert.Equal(5m, await KgOnLotAsync(api, sale.LotId));

        await owner.CancelledAsync(closer.Id(), Cancellation(closer));
        var reopened = await api.LotOfAsync(sale.LotId);
        Assert.Equal((StatusIds.HarvestLot.Reopened, (Guid?)null, 0m), (reopened.StatusId, reopened.ClosedBySalesInvoiceId, await KgOnLotAsync(api, sale.LotId)));

        var again = await sale.SoldOnDebtAsync(sale.LotId, 2m, ListPrice);
        Assert.Equal(60_000m, again.Money("total"));
    }

    private static async Task<decimal> KgOnLotAsync(AuthApiFactory api, Guid lotId)
    {
        using var scope = api.Services.CreateScope();
        var figures = await scope.ServiceProvider.GetRequiredService<IHarvestLotSalesQuery>().FiguresAsync([lotId], CancellationToken.None);
        return figures[lotId].InvoicedKg;
    }

    // SALE-08.2: a walk-in invoice given a profile is notified on that phone; kept payments on the walk-in with no answer about cash ask for a profile first; a profile is taken only then.
    [Fact]
    public async Task The_profile_of_a_cancellation_is_taken_only_for_a_walk_in_invoice_with_payments_kept()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var profile = await NewCustomerAsync(sale);

        var fullCustomer = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var answered = Cancellation(fullCustomer).With("cashGivenBack", GivenBack(Assert.Single(await api.PaymentsOfAsync(fullCustomer.Id())).Id, true));
        Assert.Equal(["Customer"], await (await owner.CancelInvoiceAsync(fullCustomer.Id(), answered.With("customerId", profile))).InvalidFieldsAsync());

        var walkIn = await sale.Manager.Client.SoldAsync(sale, sale.OtherLotId, walkIn: true);
        var walkInPayment = Assert.Single(await api.PaymentsOfAsync(walkIn.Id()));
        var givenBack = Cancellation(walkIn).With("cashGivenBack", GivenBack(walkInPayment.Id, true));
        Assert.Equal(["Customer"], await (await owner.CancelInvoiceAsync(walkIn.Id(), givenBack.With("customerId", profile))).InvalidFieldsAsync());

        // Payments kept and no profile: MSG-SALE-19 before MSG-SALE-17; nothing written by the question.
        var kept = Cancellation(walkIn).With("cashGivenBack", GivenBack(walkInPayment.Id, false));
        var needs = await (await owner.CancelInvoiceAsync(walkIn.Id(), kept)).QuestionAsync();
        Assert.Equal(("MSG-SALE-19", "customerId", 300_000m), (needs.Code, needs.Field, needs.Body.Money("owedBackAmount")));
        var merged = await NewCustomerAsync(sale);
        await api.UpdateAsync<Customer>(merged, c => c.MergedIntoCustomerId = profile);
        var refusedMerged = await (await owner.CancelInvoiceAsync(walkIn.Id(), kept.With("customerId", merged))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", refusedMerged.Text("code"));
        Assert.Equal(StatusIds.SalesInvoice.Completed, (await owner.InvoiceAsync(walkIn.Id())).GetProperty("statusId").GetInt32());
    }

    // SALE-08.1: no phone: nothing sent, nothing kept; a provider failure keeps the save. MSG-SALE-22 carries the latest total.
    [Fact]
    public async Task No_phone_sends_nothing_and_a_refused_notice_keeps_the_cancellation()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var noPhone = (await owner.CreateCustomerAsync()).Id();
        await api.UpdateAsync<Customer>(noPhone, c => c.PhoneNumber = null);
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice, customerId: noPhone);

        var answer = await owner.CancelledAsync(sold.Id(), Cancellation(sold));

        Assert.Equal(("Not sent", "Cancelled"), (answer.Text("deliveryStatus"), answer.GetProperty("invoice").Text("status")));
        Assert.Empty(await api.CancellationNoticesAsync(sold.Id()));

        var phone = await api.PhoneOfAsync(sale.CustomerId);
        var second = await sale.SoldOnDebtAsync(sale.OtherLotId, 10m, ListPrice);
        var corrected = (await owner.CorrectedAsync(second.Id(), PriceCut(second, 10_000m))).GetProperty("invoice");
        api.RefuseSmsTo(phone);
        var refused = await owner.CancelledAsync(second.Id(), Cancellation(corrected));
        Assert.Equal(("Not sent", "Cancelled"), (refused.Text("deliveryStatus"), refused.GetProperty("invoice").Text("status")));
        var row = Assert.Single(await api.CancellationNoticesAsync(second.Id()));
        Assert.Contains("(290,000 VND)", row.Body);
    }

    // SALE-07 step 9: once finished, a correction sends exactly one new message, with the latest values, to the phone of the profile; MSG-SALE-15 with (corrected); walk-in: Not sent with MSG-SALE-16.
    [Fact]
    public async Task A_finished_invoice_sends_one_corrected_message_with_the_latest_values_and_a_walk_in_shows_not_sent()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        await owner.FinishedAsync(sold.Id(), wantsQrCode: false);
        var current = await owner.InvoiceAsync(sold.Id());
        var phone = await api.PhoneOfAsync(sale.CustomerId);
        Assert.Single(api.SmsTo(phone));

        await owner.CorrectedAsync(sold.Id(), PriceCut(current, 50_000m));

        Assert.Equal(2, api.SmsTo(phone).Count);
        var text = api.SmsTo(phone)[0].Text;
        Assert.Contains("(corrected)", text);
        Assert.Contains("250,000 VND", text);
        var sendings = await api.InvoiceSendingsAsync(sold.Id());
        Assert.Equal(2, sendings.Count);
        Assert.Equal(StatusIds.MessageDelivery.Sent, sendings[^1].DeliveryStatusId);

        var walkIn = await sale.Manager.Client.SoldAsync(sale, sale.OtherLotId, walkIn: true);
        await owner.FinishedAsync(walkIn.Id(), wantsQrCode: false);
        var walkInCurrent = await owner.InvoiceAsync(walkIn.Id());
        var answer = await owner.CorrectedAsync(walkIn.Id(), DataEntry(walkInCurrent, [Line(sale.ProductId, sale.OtherLotId, 10m, ListPrice)], sale.OtherLotId));
        Assert.Equal(("Not sent", "MSG-SALE-16"), (answer.Text("deliveryStatus"), answer.Text("messageCode")));
    }

    // SALE-07 POST-5 and BR-SALE-09: when the corrected invoice cannot be queued after the commit, its delivery status must show it was not sent, not the older Sent.
    [Fact]
    public async Task A_correction_that_could_not_be_queued_shows_not_sent_instead_of_the_older_sending()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        await owner.FinishedAsync(sold.Id(), wantsQrCode: false);
        var current = await owner.InvoiceAsync(sold.Id());
        await api.UseDbAsync(db => db.Cooperatives.RemoveRange(db.Cooperatives));

        var answer = await owner.CorrectedAsync(sold.Id(), PriceCut(current, 50_000m));

        // The correction is saved and must be reported: the sending of the older invoice proves nothing about this one.
        Assert.Equal(250_000m, answer.GetProperty("invoice").Money("total"));
        Assert.Equal("Not sent", answer.Text("deliveryStatus"));
        Assert.Equal("Not sent", (await owner.InvoiceAsync(sold.Id())).Text("deliveryStatus"));
    }

    // SALE-07 and SALE-08 are Owner only, for every role; a refusal never depends on the invoice existing; a locked Owner is refused; anonymous is 401.
    [Fact]
    public async Task Only_the_cooperative_owner_changes_an_invoice_whatever_the_role()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        var zone = await api.ZoneEntityAsync(sale.ZoneId);
        var zoneAccountant = await api.StaffAsync(zone, StaffRole.Accountant);
        var allZonesAccountant = await api.StaffAsync(null, StaffRole.Accountant);
        var allZonesManager = await api.StaffAsync(null, StaffRole.ZoneManager);
        var trader = await api.TraderAsync();
        HttpClient[] refused = [sale.Manager.Client, zoneAccountant.Client, allZonesAccountant.Client, allZonesManager.Client, trader.Client];

        foreach (var client in refused)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.CorrectAsync(sold.Id(), PriceCut(sold, 1_000m))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.CancelInvoiceAsync(sold.Id(), Cancellation(sold))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.CorrectAsync(Guid.NewGuid(), PriceCut(sold, 1_000m))).StatusCode);
        }

        var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.CorrectAsync(sold.Id(), PriceCut(sold, 1_000m))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.CancelInvoiceAsync(sold.Id(), Cancellation(sold))).StatusCode);

        await api.UpdateAsync<Account>(sale.Owner.Account.Id, a => a.StatusId = StatusIds.Account.Locked);
        var lockedAnswer = await sale.Owner.Client.CorrectAsync(sold.Id(), PriceCut(sold, 1_000m));
        Assert.True(lockedAnswer.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, lockedAnswer.StatusCode.ToString());
        Assert.Equal(300_000m, await api.QueryAsync(db => db.SalesInvoices.Where(i => i.Id == sold.Id()).Select(i => i.Total).SingleAsync()));
    }

    // SALE-09.1 step 3: the offer and the refusal agree in every state, for every role.
    [Fact]
    public async Task What_is_offered_is_exactly_what_the_endpoints_accept()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var zone = await api.ZoneEntityAsync(sale.ZoneId);
        var accountant = await api.StaffAsync(zone, StaffRole.Accountant);
        var open = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        var cancelled = await sale.SoldOnDebtAsync(sale.OtherLotId, 10m, ListPrice);
        await owner.CancelledAsync(cancelled.Id(), Cancellation(cancelled));

        Assert.Equal([true, true], [(await owner.InvoiceAsync(open.Id())).Actions().Contains("Correct"), (await owner.InvoiceAsync(open.Id())).Actions().Contains("Cancel")]);
        foreach (var client in new[] { sale.Manager.Client, accountant.Client })
        {
            var actions = (await client.InvoiceAsync(open.Id())).Actions();
            Assert.DoesNotContain("Correct", actions);
            Assert.DoesNotContain("Cancel", actions);
        }

        var cancelledActions = (await owner.InvoiceAsync(cancelled.Id())).Actions();
        Assert.DoesNotContain("Correct", cancelledActions);
        Assert.DoesNotContain("Cancel", cancelledActions);
        Assert.Equal("MSG-GEN-09", (await (await owner.CorrectAsync(cancelled.Id(), PriceCut(cancelled, 1_000m))).ReadAsync(HttpStatusCode.Conflict)).Text("code"));
        Assert.Equal("MSG-GEN-09", (await (await owner.CancelInvoiceAsync(cancelled.Id(), Cancellation(cancelled))).ReadAsync(HttpStatusCode.Conflict)).Text("code"));
    }

    // Regression: a cancelled invoice leaves the end of the sale and Resend, and the list and the file count it nowhere.
    [Fact]
    public async Task A_cancelled_invoice_cannot_be_finished_or_resent_and_counts_nowhere()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice);
        await owner.CancelledAsync(sold.Id(), Cancellation(sold));

        Assert.Equal("MSG-GEN-09", (await (await owner.FinishAsync(sold.Id(), wantsQrCode: false)).ReadAsync(HttpStatusCode.Conflict)).Text("code"));
        Assert.Equal("MSG-GEN-09", (await (await owner.ResendAsync(sold.Id())).ReadAsync(HttpStatusCode.Conflict)).Text("code"));
        var list = await owner.InvoiceListAsync();
        Assert.Equal(0m, list.Money("completedTotal"));
        var row = list.Rows().Single(r => r.Id() == sold.Id());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("isPaid").ValueKind);
    }

    // Each question is asked before anything is written.
    [Fact]
    public async Task A_question_writes_nothing_and_the_reason_has_a_limit()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = await sale.Manager.Client.SoldAsync(sale, sale.LotId);

        Assert.Equal(["Reason"], await (await owner.CancelInvoiceAsync(sold.Id(), Cancellation(sold).With("reason", new string('r', 1001)))).InvalidFieldsAsync());
        Assert.Equal(["RowVersion"], await (await owner.CancelInvoiceAsync(sold.Id(), Cancellation(sold).With("rowVersion", null))).InvalidFieldsAsync());
        await (await owner.CancelInvoiceAsync(sold.Id(), Cancellation(sold))).QuestionAsync();

        Assert.Empty(await api.InvoiceAuditAsync(sold.Id(), SalesInvoiceCancellationService.CancelledAction));
        Assert.Empty(await api.CancellationNoticesAsync(sold.Id()));
        Assert.Equal(StatusIds.CustomerPayment.AwaitingConfirmation, Assert.Single(await api.PaymentsOfAsync(sold.Id())).StatusId);
        Assert.Empty(api.SmsTo(await api.PhoneOfAsync(sale.CustomerId)));
    }

    // MSG-SALE-18: only the Owners and the Accountants, each once, with the amount of the debt after the change; the Zone Manager hears nothing.
    [Fact]
    public async Task The_owed_back_notice_reaches_only_the_owners_and_accountants_with_the_amount()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var zone = await api.ZoneEntityAsync(sale.ZoneId);
        var accountant = await api.StaffAsync(zone, StaffRole.Accountant);
        var sold = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var payment = Assert.Single(await api.PaymentsOfAsync(sold.Id()));

        await owner.CancelledAsync(sold.Id(), Cancellation(sold).With("cashGivenBack", GivenBack(payment.Id, false)).With("confirmOwedBack", true));

        var notices = await api.OwedBackNoticesAsync(sold.Id());
        Assert.Equal(2, notices.Count);
        Assert.DoesNotContain(notices, n => n.RecipientAccountId == sale.Manager.Account.Id);
        Assert.All(notices, n => Assert.Contains("300,000", n.Body));
        Assert.All(notices, n => Assert.Equal("MSG-SALE-18", n.MessageCode));
        Assert.Contains(notices, n => n.RecipientAccountId == accountant.Account.Id && n.Body!.Contains("Please pay the money back"));
    }

    // SALE-08 step 7 and BR-SALE-09: the notice to the trader goes through the service and is in no notification list.
    [Fact]
    public async Task The_cancellation_notice_is_not_in_the_notification_list_of_a_trader()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var trader = await api.TraderAsync();
        var sold = await sale.SoldOnDebtAsync(sale.LotId, 10m, ListPrice, customerId: trader.Profile.Id);

        await sale.Owner.Client.CancelledAsync(sold.Id(), Cancellation(sold));

        var row = Assert.Single(await api.CancellationNoticesAsync(sold.Id()));
        Assert.NotEqual(NotificationChannel.InApp, row.Channel);
        var mine = await trader.Client.GetFromJsonAsync<JsonElement>("/api/me/notifications");
        Assert.DoesNotContain(sold.Text("code")!, mine.GetRawText());
    }

    // BR-SALE-09: the message reads the lines as saved; a line removed by the correction is gone from the kg it states; no phone is Not sent with MSG-SALE-16.
    [Fact]
    public async Task The_corrected_message_states_the_kg_as_saved_and_a_profile_without_a_phone_shows_not_sent()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = (await owner.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m), Line(sale.ProductId, sale.OtherLotId, 5m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.LotId, sale.OtherLotId))).GetProperty("invoice");
        await owner.FinishedAsync(sold.Id(), wantsQrCode: false);
        var phone = await api.PhoneOfAsync(sale.CustomerId);
        Assert.Contains("15", api.SmsTo(phone)[0].Text);

        var corrected = (await owner.CorrectedAsync(sold.Id(), DataEntry(await owner.InvoiceAsync(sold.Id()), [Line(sale.ProductId, sale.LotId, 7m, ListPrice)], sale.LotId))).GetProperty("invoice");

        Assert.Contains("7 kg", api.SmsTo(phone)[0].Text);
        Assert.DoesNotContain("15 kg", api.SmsTo(phone)[0].Text);
        Assert.Equal(210_000m, corrected.Money("total"));

        var noPhone = (await owner.CreateCustomerAsync()).Id();
        await api.UpdateAsync<Customer>(noPhone, c => c.PhoneNumber = null);
        var second = (await owner.SellAsync(Invoice(noPhone, [Line(sale.ProductId, sale.OtherLotId, 1m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.OtherLotId))).GetProperty("invoice");
        await owner.FinishedAsync(second.Id(), wantsQrCode: false);
        var answer = await owner.CorrectedAsync(second.Id(), DataEntry(await owner.InvoiceAsync(second.Id()), [Line(sale.ProductId, sale.OtherLotId, 2m, ListPrice)], sale.OtherLotId));
        Assert.Equal(("Not sent", "MSG-SALE-16"), (answer.Text("deliveryStatus"), answer.Text("messageCode")));
        Assert.Equal(60_000m, answer.GetProperty("invoice").Money("total"));
    }
}
