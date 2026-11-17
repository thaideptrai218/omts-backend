using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentSettlementTestKit;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.VietQrSaleTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>DEBT-02.6, Pay money back: Completed money paid back for all of a transfer without a customer, in the cash book, dated not before the transfer; and its cancellation.</summary>
public class CustomerPaymentSettlementPayBackTests
{
    // DEBT-02.6 step 4: the transfer is Completed with no customer, all of it is paid back; no customer debt changes; both lines are in the cash book (cash out of the Cash fund).
    [Fact]
    public async Task Paying_back_cash_completes_the_transfer_with_no_customer_and_records_the_money_paid_back_in_the_cash_book()
    {
        using var w = await StartSettlementAsync();
        var (api, sale, accountant) = (w.Api, w.Sale, w.Accountant);
        await w.TwoInvoicesAsync();
        var orphan = await api.OrphanTransferAsync(120_000m);

        var answer = await (await accountant.SettleAsync(PayBack(orphan.Id, note: "Trả lại khách"))).ReadAsync(HttpStatusCode.OK);

        var settled = answer.Settled();
        var paidBack = Assert.Single(await api.PaidBackForAsync(orphan.Id));
        Assert.Equal(
            (CustomerPaymentKind.PaidBack, 120_000m, StatusIds.CustomerPayment.Completed, (Guid?)null, CustomerPaymentMethod.Cash, (Guid?)null),
            (paidBack.Kind, paidBack.Amount, paidBack.StatusId, paidBack.CustomerId, paidBack.Method, paidBack.CashAccountId));
        Assert.NotNull(paidBack.ConfirmedAt);
        Assert.NotNull(paidBack.CollectedByAccountId);
        Assert.Equal(paidBack.Id, settled.GetProperty("paidBack").Id());
        Assert.Equal(orphan.Id, settled.GetProperty("paidBack").GetProperty("paidBackForPaymentId").GetGuid());
        Assert.Equal(JsonValueKind.Null, settled.GetProperty("debtAfter").ValueKind);
        var transfer = await api.PaymentByIdAsync(orphan.Id);
        Assert.Equal((StatusIds.CustomerPayment.Completed, (Guid?)null), (transfer.StatusId, transfer.CustomerId));
        Assert.Equal(900_000m, await sale.Owner.Client.CustomerDebtAsync(sale.CustomerId));
        Assert.Equal(1, await api.AuditCountAsync(paidBack.Id, "CustomerPayment.Recorded"));
        Assert.Contains(paidBack.Code, await api.AuditAfterAsync(orphan.Id, "CustomerPayment.Settled"));
        Assert.Contains("The customer debts have not changed.", Assert.Single(await api.TransactionNoticesAsync(sale.Owner.Account.Id)).Body);

        var receipt = Assert.Single(await accountant.CashBookLinesAsync("Bank", orphan.Code));
        Assert.Equal(("CustomerPayment", "Receipt", 120_000m), (receipt.Text("source"), receipt.Text("direction"), receipt.Money("amount")));
        var payment = Assert.Single(await accountant.CashBookLinesAsync("Cash", paidBack.Code));
        Assert.Equal(("CustomerPaidBack", "Payment", 120_000m), (payment.Text("source"), payment.Text("direction"), payment.Money("amount")));
    }

    // A transfer is paid back from a bank account of the cash book: the one it arrived in unless another is chosen.
    [Fact]
    public async Task Paying_back_by_transfer_leaves_the_account_the_money_arrived_in_unless_another_bank_account_is_chosen()
    {
        using var w = await StartSettlementAsync();
        var (api, _, accountant) = (w.Api, w.Sale, w.Accountant);
        var other = await accountant.BankAccountAsync(OtherAccountNumber);
        var first = await api.OrphanTransferAsync(120_000m);
        var second = await api.OrphanTransferAsync(80_000m);

        await (await accountant.SettleAsync(
            PayBack(first.Id, "OtherTransfer", reference: CustomerPaymentTestKit.NewReference()),
            PayBack(second.Id, "OtherTransfer", reference: CustomerPaymentTestKit.NewReference(), cashAccountId: other))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(w.BankAccountId, Assert.Single(await api.PaidBackForAsync(first.Id)).CashAccountId);
        var paidBack = Assert.Single(await api.PaidBackForAsync(second.Id));
        Assert.Equal((other, CustomerPaymentMethod.OtherTransfer), (paidBack.CashAccountId!.Value, paidBack.Method));
        var line = Assert.Single(await accountant.CashBookLinesAsync("Bank", paidBack.Code));
        Assert.Equal(("CustomerPaidBack", "Payment", other), (line.Text("source"), line.Text("direction"), line.GetProperty("cashAccount").Id()));
    }

    // PM decision 12/11 (3): the date of the money paid back cannot be before the date of the transfer; the day of the transfer is allowed.
    [Fact]
    public async Task Money_paid_back_cannot_be_dated_before_the_transfer()
    {
        using var w = await StartSettlementAsync();
        var (api, _, accountant) = (w.Api, w.Sale, w.Accountant);
        var orphan = await api.OrphanTransferAsync(120_000m, occurredAt: DateTimeOffset.UtcNow.AddDays(-3));
        var transferDate = orphan.PaymentDate;
        Assert.True(transferDate < PurchaseRequestTestKit.Today);

        Assert.Equal(["Payment date"], await (await accountant.SettleAsync(PayBack(orphan.Id, date: transferDate.AddDays(-1)))).InvalidFieldsAsync());
        Assert.Equal(["Payment date"], await (await accountant.SettleAsync(PayBack(orphan.Id, date: PurchaseRequestTestKit.Today.AddDays(1)))).InvalidFieldsAsync());
        Assert.Empty(await api.PaidBackForAsync(orphan.Id));

        await (await accountant.SettleAsync(PayBack(orphan.Id, date: transferDate))).ReadAsync(HttpStatusCode.OK);

        var paidBack = Assert.Single(await api.PaidBackForAsync(orphan.Id));
        Assert.Equal(transferDate, paidBack.PaymentDate);
        Assert.True(paidBack.PaidAt >= orphan.PaidAt);
    }

    // DEBT-02.6.E1 and BR-DEBT-03: what is missing or wrong is named; a refused list saves nothing.
    [Fact]
    public async Task A_pay_back_with_missing_or_invalid_data_is_refused_and_nothing_is_saved()
    {
        using var w = await StartSettlementAsync();
        var (api, _, accountant) = (w.Api, w.Sale, w.Accountant);
        var orphan = await api.OrphanTransferAsync(120_000m);

        Assert.Equal(["Bank reference"], await (await accountant.SettleAsync(PayBack(orphan.Id, "OtherTransfer"))).InvalidFieldsAsync());
        Assert.Equal(["Method"], await (await accountant.SettleAsync(PayBack(orphan.Id, "VietQrInvoice"))).InvalidFieldsAsync());
        Assert.Equal(["Cash account"], await (await accountant.SettleAsync(PayBack(orphan.Id, "Cash", cashAccountId: w.BankAccountId))).InvalidFieldsAsync());
        Assert.Equal(["Cash account"], await (await accountant.SettleAsync(PayBack(orphan.Id, "OtherTransfer", reference: CustomerPaymentTestKit.NewReference(), cashAccountId: Guid.NewGuid()))).InvalidFieldsAsync());
        Assert.Equal(["Note"], await (await accountant.SettleAsync(PayBack(orphan.Id, note: new string('x', 1001)))).InvalidFieldsAsync());

        Assert.Equal(StatusIds.CustomerPayment.AwaitingConfirmation, (await api.PaymentByIdAsync(orphan.Id)).StatusId);
        Assert.Empty(await api.PaidBackForAsync(orphan.Id));
        Assert.Empty(await accountant.CashBookLinesAsync("Bank", orphan.Code));
    }

    // MSG-DEBT-05: a bank reference already on a payment that is not cancelled is asked about; so is the reference of an earlier pay back of the same list.
    [Fact]
    public async Task A_bank_reference_already_recorded_is_asked_about_before_paying_back()
    {
        using var w = await StartSettlementAsync();
        var (api, _, accountant) = (w.Api, w.Sale, w.Accountant);
        var reference = CustomerPaymentTestKit.NewReference();
        var orphan = await api.OrphanTransferAsync(120_000m, reference);

        var question = await (await accountant.SettleAsync(PayBack(orphan.Id, "OtherTransfer", reference: reference))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-DEBT-05", "confirmDuplicateBankReference"), (question.Text("code"), question.Text("confirmationField")));
        Assert.Equal(orphan.Id, question.GetProperty("settlementPaymentId").GetGuid());

        await (await accountant.SettleAsync(PayBack(orphan.Id, "OtherTransfer", reference: reference, confirmDuplicateBankReference: true))).ReadAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Two_pay_backs_of_one_list_with_one_new_bank_reference_are_asked_about()
    {
        using var w = await StartSettlementAsync();
        var (api, _, accountant) = (w.Api, w.Sale, w.Accountant);
        var first = await api.OrphanTransferAsync(120_000m);
        var second = await api.OrphanTransferAsync(80_000m);
        var reference = CustomerPaymentTestKit.NewReference();

        var question = await (await accountant.SettleAsync(
            PayBack(first.Id, "OtherTransfer", reference: reference), PayBack(second.Id, "OtherTransfer", reference: reference))).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal("MSG-DEBT-05", question.Text("code"));
        Assert.Equal((first.Id, second.Id), (question.GetProperty("paymentId").GetGuid(), question.GetProperty("settlementPaymentId").GetGuid()));
        Assert.Empty(await api.PaidBackForAsync(first.Id));
    }

    // DEBT-02.4 allows cancelling money paid back: the payment goes back to the list (out of the cash book) and is settled again; while its money paid back stands it cannot be cancelled.
    [Fact]
    public async Task Cancelling_the_money_paid_back_of_a_settlement_puts_the_payment_back_on_the_list_and_a_replay_records_nothing()
    {
        using var w = await StartSettlementAsync();
        var (api, sale, accountant) = (w.Api, w.Sale, w.Accountant);
        var reference = CustomerPaymentTestKit.NewReference();
        var orphan = await api.OrphanTransferAsync(120_000m, reference);
        await (await accountant.SettleAsync(PayBack(orphan.Id))).ReadAsync(HttpStatusCode.OK);
        var paidBack = Assert.Single(await api.PaidBackForAsync(orphan.Id));

        var refused = await (await accountant.CancelAsync(orphan.Id, "Mistake")).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", refused.Text("code"));
        Assert.True(refused.GetProperty("hasMoneyPaidBack").GetBoolean());
        Assert.Equal("AlreadyRecorded", (await api.TransferAsync(orphan.TransferInvoiceCode, 120_000m, reference)).Outcome());

        await (await accountant.CancelAsync(paidBack.Id, "Paid to the wrong person")).ReadAsync(HttpStatusCode.OK);

        var row = await api.PaymentByIdAsync(orphan.Id);
        Assert.Equal((StatusIds.CustomerPayment.AwaitingConfirmation, (Guid?)null, (DateTimeOffset?)null), (row.StatusId, row.ConfirmedByAccountId, row.ConfirmedAt));
        Assert.Equal(1, await api.AuditCountAsync(orphan.Id, "CustomerPayment.SettlementUndone"));
        Assert.True((await accountant.AwaitingRowAsync(orphan.Id)).GetProperty("needsSettlement").GetBoolean());
        Assert.Empty(await accountant.CashBookLinesAsync("Bank", orphan.Code));
        Assert.Equal("Cancelled", Assert.Single(await accountant.CashBookLinesAsync("Cash", paidBack.Code)).Text("status"));
        Assert.Equal("MSG-GEN-09", (await (await accountant.CancelAsync(paidBack.Id, "Again")).ReadAsync(HttpStatusCode.Conflict)).Text("code"));

        await (await accountant.SettleAsync(ToCustomer(orphan.Id, sale.CustomerId))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(-120_000m, await sale.Owner.Client.CustomerDebtAsync(sale.CustomerId));
        Assert.Equal(StatusIds.CustomerPayment.Completed, (await api.PaymentByIdAsync(orphan.Id)).StatusId);
        Assert.Single(await accountant.CashBookLinesAsync("Bank", orphan.Code));
    }
}
