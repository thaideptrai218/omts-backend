using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Payments;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>DEBT-02.2 step 2 and DEBT-02.6 as pure rules: which payment is marked, the choice of a settlement, the parts an attached payment pays, and the date of the money paid back.</summary>
public class CustomerPaymentSettlementRulesTests
{
    private static readonly DateOnly TransferDate = new(2026, 11, 10);
    private static readonly DateTimeOffset TransferAt = new(2026, 11, 10, 14, 30, 0, TimeSpan.FromHours(7));

    private static CustomerPayment Held(decimal amount = 120_000m, int status = StatusIds.CustomerPayment.AwaitingConfirmation) => new()
    {
        Amount = amount, Code = "PT000001", Kind = CustomerPaymentKind.Payment, Method = CustomerPaymentMethod.VietQrInvoice, StatusId = status, PaymentDate = TransferDate, PaidAt = TransferAt,
    };

    private static UnpaidInvoice Unpaid(string code, int day, decimal unpaid, decimal total = 0m) =>
        new(Guid.NewGuid(), code, TransferDate.AddDays(day - 20), total == 0m ? unpaid : total, (total == 0m ? unpaid : total) - unpaid, unpaid, 20 - day);

    private static CustomerDebtAllocation Reading(decimal debt, params UnpaidInvoice[] unpaid) => new(debt, unpaid, null, null);

    [Fact]
    public void Only_a_payment_awaiting_confirmation_with_no_customer_and_no_invoice_is_marked()
    {
        Assert.True(CustomerPaymentSettlementRules.NeedsSettlement(Held()));

        var onProfile = Held();
        onProfile.CustomerId = Guid.NewGuid();
        var onInvoice = Held();
        onInvoice.InvoiceId = Guid.NewGuid();
        var paidBack = Held();
        paidBack.Kind = CustomerPaymentKind.PaidBack;

        Assert.False(CustomerPaymentSettlementRules.NeedsSettlement(onProfile));
        Assert.False(CustomerPaymentSettlementRules.NeedsSettlement(onInvoice));
        Assert.False(CustomerPaymentSettlementRules.NeedsSettlement(paidBack));
        Assert.False(CustomerPaymentSettlementRules.NeedsSettlement(Held(status: StatusIds.CustomerPayment.Completed)));
        Assert.False(CustomerPaymentSettlementRules.NeedsSettlement(Held(status: StatusIds.CustomerPayment.Cancelled)));
        Assert.False(CustomerPaymentSettlementRules.NeedsSettlement(Held(status: StatusIds.CustomerPayment.PendingTransfer)));
    }

    [Fact]
    public void A_settlement_names_a_customer_profile_or_a_pay_back_and_never_both_or_neither()
    {
        var id = Guid.NewGuid();
        var payBack = new SettlementPayBack(TransferDate, CustomerPaymentMethod.Cash);

        Assert.True(CustomerPaymentSettlementRules.ChoosesOneWay(new SettleInConfirmations(id, CustomerId: Guid.NewGuid())));
        Assert.True(CustomerPaymentSettlementRules.ChoosesOneWay(new SettleInConfirmations(id, PayBack: payBack)));
        Assert.False(CustomerPaymentSettlementRules.ChoosesOneWay(new SettleInConfirmations(id)));
        Assert.False(CustomerPaymentSettlementRules.ChoosesOneWay(new SettleInConfirmations(id, Guid.NewGuid(), PayBack: payBack)));
    }

    // PM decision of 12/11: the money paid back cannot be dated before the transfer it pays back.
    [Fact]
    public void Money_paid_back_cannot_be_dated_before_the_transfer_and_never_earlier_in_the_day_than_it()
    {
        var transfer = Held();

        Assert.True(CustomerPaymentSettlementRules.PaidBackBeforeTransfer(TransferDate.AddDays(-1), transfer));
        Assert.False(CustomerPaymentSettlementRules.PaidBackBeforeTransfer(TransferDate, transfer));
        Assert.False(CustomerPaymentSettlementRules.PaidBackBeforeTransfer(TransferDate.AddDays(3), transfer));
        Assert.False(CustomerPaymentSettlementRules.PaidBackBeforeTransfer(null, transfer));

        var startOfTransferDay = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.FromHours(7));
        Assert.Equal(TransferAt, CustomerPaymentSettlementRules.PaidBackAt(startOfTransferDay, transfer));
        Assert.Equal(TransferAt.AddHours(2), CustomerPaymentSettlementRules.PaidBackAt(TransferAt.AddHours(2), transfer));
    }

    [Fact]
    public void The_parts_sent_for_an_attached_payment_must_be_unpaid_invoices_of_the_profile_and_at_most_what_each_has_left()
    {
        var older = Unpaid("HD1", 0, 300_000m);
        var newer = Unpaid("HD2", 1, 200_000m);
        var reading = Reading(500_000m, older, newer);

        var parts = CustomerPaymentAllocationParts.Of([new CustomerPaymentAllocationInput(newer.Id, 120_000m)], 120_000m, reading, oldestFirstWhenNone: true, out var error);
        Assert.Null(error);
        Assert.Equal([(newer.Id, 120_000m)], parts);

        CustomerPaymentAllocationParts.Of([new CustomerPaymentAllocationInput(Guid.NewGuid(), 10_000m)], 120_000m, reading, true, out var unknown);
        Assert.Equal("Allocation 1", unknown!.Details!["field"]);

        CustomerPaymentAllocationParts.Of([new CustomerPaymentAllocationInput(newer.Id, 200_001m)], 300_000m, reading, true, out var tooMuch);
        Assert.Equal("Allocation 1", tooMuch!.Details!["field"]);
    }

    [Fact]
    public void Without_parts_an_attached_payment_covers_the_oldest_unpaid_invoices_first_and_an_empty_list_names_none()
    {
        var older = Unpaid("HD1", 0, 100_000m);
        var newer = Unpaid("HD2", 1, 200_000m);
        var reading = Reading(300_000m, older, newer);

        Assert.Equal([(older.Id, 100_000m), (newer.Id, 20_000m)], CustomerPaymentAllocationParts.Of(null, 120_000m, reading, oldestFirstWhenNone: true, out _));
        Assert.Equal([(older.Id, 100_000m), (newer.Id, 200_000m)], CustomerPaymentAllocationParts.Of(null, 900_000m, reading, oldestFirstWhenNone: true, out _));
        Assert.Empty(CustomerPaymentAllocationParts.Of([], 120_000m, reading, oldestFirstWhenNone: true, out _));
        Assert.Empty(CustomerPaymentAllocationParts.Of(null, 120_000m, reading, oldestFirstWhenNone: false, out _));
    }

    [Fact]
    public void A_second_payment_of_one_list_reads_what_the_first_one_paid()
    {
        var invoice = Unpaid("HD1", 0, 100_000m, total: 150_000m);
        var reading = Reading(100_000m, invoice);

        var afterFirst = CustomerPaymentAllocationParts.After(reading, [(invoice.Id, 60_000m)], 70_000m);

        Assert.Equal(30_000m, afterFirst.Debt);
        Assert.Equal((40_000m, 110_000m), (afterFirst.UnpaidInvoices.Single().Unpaid, afterFirst.UnpaidInvoices.Single().Covered));
        var afterSecond = CustomerPaymentAllocationParts.After(afterFirst, [(invoice.Id, 40_000m)], 40_000m);
        Assert.Empty(afterSecond.UnpaidInvoices);
        Assert.Equal(-10_000m, afterSecond.Debt);

        CustomerPaymentAllocationParts.Of([new CustomerPaymentAllocationInput(invoice.Id, 50_000m)], 50_000m, afterFirst, true, out var error);
        Assert.NotNull(error);
    }

    [Fact]
    public void The_shape_of_the_parts_is_checked_as_for_any_payment()
    {
        var invoice = Guid.NewGuid();

        Assert.Empty(CustomerPaymentRules.InvalidAllocations(null, 100_000m));
        Assert.Empty(CustomerPaymentRules.InvalidAllocations([new CustomerPaymentAllocationInput(invoice, 100_000m)], 100_000m));
        Assert.Equal(["Allocations"], CustomerPaymentRules.InvalidAllocations([new CustomerPaymentAllocationInput(invoice, 100_001m)], 100_000m));
        Assert.Equal(["Allocation 1"], CustomerPaymentRules.InvalidAllocations([new CustomerPaymentAllocationInput(invoice, 0m)], 100_000m));
        Assert.Equal(["Allocation 1"], CustomerPaymentRules.InvalidAllocations([new CustomerPaymentAllocationInput(null, 10m)], 100_000m));
        Assert.Contains("Allocations", CustomerPaymentRules.InvalidAllocations([new CustomerPaymentAllocationInput(invoice, 10m), new CustomerPaymentAllocationInput(invoice, 20m)], 100_000m));
    }

    // BR-DEBT-06: one notice for the list names what was settled, and the debt of a profile a payment was attached to.
    [Fact]
    public void The_notice_of_a_list_names_the_payments_settled_and_the_debt_of_the_profiles_they_were_attached_to()
    {
        var notice = UserMessages.CustomerPaymentsBatch(
            "Kế toán Hà", [], [], [("Bà Lan", 180_000m)], [("PT000007", 120_000m), ("PT000008", 40_000m)]);

        Assert.Equal("MSG-DEBT-03", notice.Code);
        Assert.Contains("settled 2 payments (PT000007, PT000008) of 160,000 VND", notice.Text);
        Assert.Contains("Bà Lan", notice.Text);
        var parameters = notice.Parameters!;
        Assert.Equal((2, 160_000m, 160_000m), ((int)parameters["settled_count"]!, (decimal)parameters["settled_amount"]!, (decimal)parameters["amount"]!));
        Assert.Equal(0, (int)parameters["settled_more_count"]!);

        var unchanged = UserMessages.CustomerPaymentsBatch("Kế toán Hà", [("PT000001", 4_000_000m)], [], []);
        Assert.EndsWith("The customer debts have not changed.", unchanged.Text);
        Assert.Equal(0, (int)unchanged.Parameters!["settled_count"]!);
    }

    [Fact]
    public void A_pay_back_only_list_still_says_the_debts_have_not_changed()
    {
        var notice = UserMessages.CustomerPaymentsBatch("Kế toán Hà", [], [], [], [("PT000007", 120_000m)]);

        Assert.EndsWith("The customer debts have not changed.", notice.Text);
    }
}
