using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentSettlementTestKit;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.VietQrSaleTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// DEBT-02.2 (the mark) and DEBT-02.6 (settle: attach a payment without a customer to a profile) through the API, on the allocation model of the customer payments: the payment is
/// Completed, set against the invoices the user chooses or the oldest, and with the bank account it arrived in it enters the cash book.
/// </summary>
public class CustomerPaymentSettlementTests
{
    // DEBT-02.2 steps 2-3, BR-DEBT-03: only a transfer with no customer and no saved invoice is marked; it is settled, never confirmed plainly.
    [Fact]
    public async Task A_transfer_with_no_customer_and_no_invoice_is_marked_and_cannot_be_confirmed_plainly_while_cash_at_the_port_confirms_as_before()
    {
        using var w = await StartSettlementAsync();
        var (api, sale, accountant) = (w.Api, w.Sale, w.Accountant);
        var cash = await api.PortCashOfAsync(await sale.PayLaterAsync(sale.CustomerId, 10m, cash: 100_000m));
        var orphan = await api.OrphanTransferAsync(120_000m);

        var rows = (await accountant.AwaitingRowsAsync()).ToDictionary(r => r.Id());

        Assert.True(rows[orphan.Id].GetProperty("needsSettlement").GetBoolean());
        Assert.False(rows[cash].GetProperty("needsSettlement").GetBoolean());
        Assert.True((await (await accountant.GetPaymentAsync(orphan.Id)).ReadAsync(HttpStatusCode.OK)).GetProperty("needsSettlement").GetBoolean());
        var refused = await (await accountant.ConfirmAsync([orphan.Id])).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", refused.Text("code"));
        Assert.True(refused.GetProperty("needsSettlement").GetBoolean());
        Assert.Equal(StatusIds.CustomerPayment.AwaitingConfirmation, (await api.PaymentByIdAsync(orphan.Id)).StatusId);
        Assert.Equal(HttpStatusCode.OK, (await accountant.ConfirmAsync([cash])).StatusCode);
    }

    // PM decision 12/11 (1), (2): attached, the payment is allocated like every other payment (the oldest invoices first), Completed, counted in the debt and in the cash book.
    [Fact]
    public async Task Attaching_a_transfer_to_a_profile_completes_it_pays_the_oldest_invoices_first_and_puts_it_in_the_cash_book()
    {
        using var w = await StartSettlementAsync();
        var (api, sale, accountant) = (w.Api, w.Sale, w.Accountant);
        var (older, newer) = await w.TwoInvoicesAsync();
        var orphan = await api.OrphanTransferAsync(420_000m);
        Assert.Equal(w.BankAccountId, orphan.CashAccountId);
        Assert.Empty(await accountant.CashBookLinesAsync("Bank", orphan.Code));

        var answer = await (await accountant.SettleAsync(ToCustomer(orphan.Id, sale.CustomerId))).ReadAsync(HttpStatusCode.OK);

        var settled = answer.Settled();
        var payment = settled.GetProperty("payment");
        Assert.Equal(480_000m, settled.Money("debtAfter"));
        Assert.Equal(JsonValueKind.Null, settled.GetProperty("paidBack").ValueKind);
        Assert.Equal((StatusIds.CustomerPayment.Completed, "Completed", sale.CustomerId), (payment.GetProperty("statusId").GetInt32(), payment.Text("status"), payment.GetProperty("customer").Id()));
        Assert.Equal(
            [(older.Text("code")!, 300_000m), (newer.Text("code")!, 120_000m)],
            payment.GetProperty("allocations").EnumerateArray().Select(a => (a.Text("invoiceCode")!, a.Money("amount"))));
        Assert.Equal([(older.Text("code")!, 300_000m), (newer.Text("code")!, 120_000m)], await api.AllocationsOfAsync(orphan.Id));
        Assert.Equal((480_000m, 480_000m, 480_000m), await sale.Owner.Client.DebtsEverywhereAsync(sale.CustomerId));
        Assert.Equal([(newer.Text("code")!, 480_000m)], (await accountant.StatementAsync(sale.CustomerId)).Unpaid());
        Assert.DoesNotContain(orphan.Id, (await accountant.AwaitingRowsAsync()).Select(r => r.Id()));

        var row = await api.PaymentByIdAsync(orphan.Id);
        Assert.Equal((StatusIds.CustomerPayment.Completed, sale.CustomerId, w.BankAccountId), (row.StatusId, row.CustomerId!.Value, row.CashAccountId!.Value));
        Assert.NotNull(row.ConfirmedByAccountId);
        Assert.NotNull(row.ConfirmedAt);
        Assert.Contains("AttachedToCustomer", await api.AuditAfterAsync(orphan.Id, "CustomerPayment.Settled"));

        var line = Assert.Single(await accountant.CashBookLinesAsync("Bank", orphan.Code));
        Assert.Equal(("CustomerPayment", "Receipt", 420_000m), (line.Text("source"), line.Text("direction"), line.Money("amount")));
        Assert.Equal(w.BankAccountId, line.GetProperty("cashAccount").Id());
        Assert.NotEqual(JsonValueKind.Null, line.GetProperty("party").ValueKind);

        var notice = Assert.Single(await api.TransactionNoticesAsync(sale.Owner.Account.Id));
        Assert.Contains($"settled 1 payment ({orphan.Code}) of 420,000 VND", notice.Body);
        Assert.Contains("owes", notice.Body);
        Assert.Equal(orphan.Id, notice.RecordId);
        var parameters = JsonDocument.Parse(notice.Params!).RootElement;
        Assert.Equal(("Confirmations", 1, 420_000m), (parameters.Text("transaction_kind"), parameters.GetProperty("settled_count").GetInt32(), parameters.Money("settled_amount")));
    }

    // PM decision 12/11 (1): the invoices the user chooses are paid first; what is left covers the oldest ones as the debt reading does for any payment.
    [Fact]
    public async Task Attaching_with_chosen_invoices_pays_those_first_and_the_rest_covers_the_oldest()
    {
        using var w = await StartSettlementAsync();
        var (api, sale, accountant) = (w.Api, w.Sale, w.Accountant);
        var (older, newer) = await w.TwoInvoicesAsync();
        var orphan = await api.OrphanTransferAsync(120_000m);
        var newerId = newer.Id();

        var answer = await (await accountant.SettleAsync(ToCustomer(orphan.Id, sale.CustomerId, (newerId, 100_000m)))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(780_000m, answer.Settled().Money("debtAfter"));
        Assert.Equal([(newer.Text("code")!, 100_000m)], await api.AllocationsOfAsync(orphan.Id));
        Assert.Equal([(older.Text("code")!, 280_000m), (newer.Text("code")!, 500_000m)], (await accountant.StatementAsync(sale.CustomerId)).Unpaid());
        Assert.Equal(780_000m, await sale.Owner.Client.CustomerDebtAsync(sale.CustomerId));
    }

    // An empty list of parts only takes the money off the debt (as a payment recorded with no parts).
    [Fact]
    public async Task Attaching_with_an_empty_list_of_parts_only_lowers_the_debt()
    {
        using var w = await StartSettlementAsync();
        var (api, sale, accountant) = (w.Api, w.Sale, w.Accountant);
        await w.TwoInvoicesAsync();
        var orphan = await api.OrphanTransferAsync(120_000m);

        await (await accountant.SettleAsync(new { id = orphan.Id, customerId = sale.CustomerId, allocations = Array.Empty<object>() })).ReadAsync(HttpStatusCode.OK);

        Assert.Empty(await api.AllocationsOfAsync(orphan.Id));
        Assert.Equal(780_000m, await sale.Owner.Client.CustomerDebtAsync(sale.CustomerId));
    }

    // The parts are checked as for any payment, and a refused list saves nothing.
    [Fact]
    public async Task Parts_that_are_not_unpaid_invoices_of_the_profile_or_pay_too_much_are_refused_and_nothing_is_saved()
    {
        using var w = await StartSettlementAsync();
        var (api, sale, accountant) = (w.Api, w.Sale, w.Accountant);
        var (older, newer) = await w.TwoInvoicesAsync();
        var orphan = await api.OrphanTransferAsync(400_000m);
        var otherCustomer = (await sale.Owner.Client.CreateCustomerAsync()).Id();
        var foreign = (await sale.Manager.Client.SellAsync(Invoice(otherCustomer, [Line(sale.ProductId, sale.OtherLotId, 1m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.OtherLotId)))
            .GetProperty("invoice").Id();

        Assert.Equal(["Allocation 1"], await (await accountant.SettleAsync(ToCustomer(orphan.Id, sale.CustomerId, (foreign, 10_000m)))).InvalidFieldsAsync());
        Assert.Equal(["Allocation 1"], await (await accountant.SettleAsync(ToCustomer(orphan.Id, sale.CustomerId, (older.Id(), 300_001m)))).InvalidFieldsAsync());
        Assert.Equal(["Allocations"], await (await accountant.SettleAsync(ToCustomer(orphan.Id, sale.CustomerId, (older.Id(), 300_000m), (newer.Id(), 100_001m)))).InvalidFieldsAsync());
        Assert.Equal(["Allocation 1"], await (await accountant.SettleAsync(ToCustomer(orphan.Id, sale.CustomerId, (older.Id(), 0m)))).InvalidFieldsAsync());

        Assert.Equal(StatusIds.CustomerPayment.AwaitingConfirmation, (await api.PaymentByIdAsync(orphan.Id)).StatusId);
        Assert.Empty(await api.AllocationsOfAsync(orphan.Id));
        Assert.Equal(900_000m, await sale.Owner.Client.CustomerDebtAsync(sale.CustomerId));
    }

    // Two payments of one list attached to one profile read the profile once: the second cannot pay what the first one paid.
    [Fact]
    public async Task Two_payments_of_one_list_attached_to_one_profile_cannot_pay_one_invoice_twice()
    {
        using var w = await StartSettlementAsync();
        var (api, sale, accountant) = (w.Api, w.Sale, w.Accountant);
        var older = await sale.PayLaterAsync(sale.CustomerId, 10m);
        var first = await api.OrphanTransferAsync(200_000m);
        var second = await api.OrphanTransferAsync(200_000m);
        var invoice = older.Id();

        var refused = await (await accountant.SettleAsync(ToCustomer(first.Id, sale.CustomerId, (invoice, 200_000m)), ToCustomer(second.Id, sale.CustomerId, (invoice, 200_000m)))).ReadAsync(HttpStatusCode.BadRequest);

        Assert.Equal(second.Id, refused.GetProperty("paymentId").GetGuid());
        Assert.Equal("Allocation 1", refused.Text("field"));
        Assert.Equal(StatusIds.CustomerPayment.AwaitingConfirmation, (await api.PaymentByIdAsync(first.Id)).StatusId);

        var answer = await (await accountant.SettleAsync(ToCustomer(first.Id, sale.CustomerId, (invoice, 200_000m)), ToCustomer(second.Id, sale.CustomerId))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal([-100_000m, -100_000m], answer.GetProperty("settled").EnumerateArray().Select(s => s.Money("debtAfter")));
        Assert.Equal([(older.Text("code")!, 100_000m)], await api.AllocationsOfAsync(second.Id));
        Assert.Equal(-100_000m, await sale.Owner.Client.CustomerDebtAsync(sale.CustomerId));
    }

    // DEBT-02.2 step 6, BR-DEBT-06: one notice for the list; the debts it tells are the ones the list changed.
    [Fact]
    public async Task Confirmed_cancelled_and_settled_payments_of_one_list_give_one_notice_and_one_save()
    {
        using var w = await StartSettlementAsync();
        var (api, sale, accountant) = (w.Api, w.Sale, w.Accountant);
        var cash = await api.PortCashOfAsync(await sale.PayLaterAsync(sale.CustomerId, 10m, cash: 100_000m));
        var otherCash = await api.PortCashOfAsync(await sale.PayLaterAsync(sale.CustomerId, 10m, cash: 50_000m));
        var attached = await api.OrphanTransferAsync(70_000m);
        var paidBack = await api.OrphanTransferAsync(40_000m);

        var answer = await (await accountant.ConfirmCancelSettleAsync(
            [cash], [(otherCash, "Not handed over")], [ToCustomer(attached.Id, sale.CustomerId), PayBack(paidBack.Id)])).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(2, answer.GetProperty("settled").GetArrayLength());
        var notice = Assert.Single(await api.TransactionNoticesAsync(sale.Owner.Account.Id));
        Assert.Contains("confirmed 1 payment", notice.Body);
        Assert.Contains("cancelled 1 payment", notice.Body);
        Assert.Contains("settled 2 payments", notice.Body);
        Assert.Null(notice.RecordId);
        Assert.Equal(600_000m - 100_000m - 70_000m, await sale.Owner.Client.CustomerDebtAsync(sale.CustomerId));
    }

    // PM decision 12/11 (4): a settlement sent again (the answer was lost) is MSG-GEN-09 and applies nothing twice.
    [Fact]
    public async Task A_settlement_sent_again_answers_MSG_GEN_09_and_applies_nothing_twice()
    {
        using var w = await StartSettlementAsync();
        var (api, sale, accountant) = (w.Api, w.Sale, w.Accountant);
        await w.TwoInvoicesAsync();
        var orphan = await api.OrphanTransferAsync(120_000m);
        await (await accountant.SettleAsync(ToCustomer(orphan.Id, sale.CustomerId))).ReadAsync(HttpStatusCode.OK);

        var again = await (await accountant.SettleAsync(ToCustomer(orphan.Id, sale.CustomerId))).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal("MSG-GEN-09", again.Text("code"));
        Assert.Equal(orphan.Id, again.GetProperty("paymentId").GetGuid());
        Assert.Equal(1, await api.AuditCountAsync(orphan.Id, "CustomerPayment.Settled"));
        Assert.Single(await api.AllocationsOfAsync(orphan.Id));
        Assert.Equal(780_000m, await sale.Owner.Client.CustomerDebtAsync(sale.CustomerId));
        Assert.Single(await api.TransactionNoticesAsync(sale.Owner.Account.Id));
        Assert.Equal(HttpStatusCode.Conflict, (await accountant.SettleAsync(PayBack(orphan.Id))).StatusCode);
        Assert.Empty(await api.PaidBackForAsync(orphan.Id));
    }

    // A transfer short of a sale still awaiting it counts in the sale's received amount until the Accountant settles it.
    [Fact]
    public async Task A_short_transfer_of_a_pending_sale_stops_counting_for_the_sale_once_it_is_settled()
    {
        using var w = await StartSettlementAsync();
        var (api, sale, accountant) = (w.Api, w.Sale, w.Accountant);
        var code = (await sale.Manager.Client.AwaitTransferAsync(Invoice(null, [Line(sale.ProductId, sale.LotId, 10m)], VietQr(), sale.LotId))).Code();
        Assert.Equal("WithoutCustomer", (await api.TransferAsync(code, 100_000m)).Outcome());
        var held = await api.PaymentOfCodeAsync(code);
        Assert.Equal(100_000m, (await sale.Manager.Client.PolledAsync(code)).Money("receivedAmount"));

        await (await accountant.SettleAsync(ToCustomer(held.Id, sale.CustomerId))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(0m, (await sale.Manager.Client.PolledAsync(code)).Money("receivedAmount"));
    }
}
