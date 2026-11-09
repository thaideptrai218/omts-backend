using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>Independent QA of the money of SALE-07 and SALE-08: an oracle written from BR-DEBT-02, not from the code.</summary>
public class SalesInvoiceChangeMoneyQaTests
{
    // BR-DEBT-02: debt = invoices − payments; a change of the invoice moves the debt by exactly the difference of the totals.
    [Theory]
    [InlineData(10_000_000, 10_000_000, 9_500_000, 9_500_000)]
    [InlineData(0, 300_000, 300_000, 0)]
    [InlineData(0, 300_000, 0, -300_000)]
    [InlineData(-100, 50, 80, -70)]
    public void A_correction_moves_the_debt_by_the_difference_of_the_totals(int debtBefore, int totalBefore, int totalAfter, int debtAfter) =>
        Assert.Equal((decimal)debtAfter, SalesInvoiceChangeMoney.AfterCorrection(debtBefore, totalBefore, totalAfter));

    // Moving an invoice keeps the sum of the two debts when the total does not change, whatever was paid.
    [Theory]
    [InlineData(500, 0, 300, 100)]
    [InlineData(0, 700, 300, 0)]
    [InlineData(100, -50, 300, 400)]
    public void Moving_an_invoice_keeps_the_sum_of_the_two_debts(int oldDebt, int newDebt, int total, int paid)
    {
        var left = SalesInvoiceChangeMoney.AfterLeaving(oldDebt, total, paid);
        var came = SalesInvoiceChangeMoney.AfterComingIn(newDebt, total, paid);

        Assert.Equal((decimal)(oldDebt + newDebt), left + came);
    }

    // BR-SALE-11: only the cash given back no longer counts; a kept payment still lowers the debt.
    [Theory]
    [InlineData(300, 300, 0, 0)]
    [InlineData(300, 300, 300, 300)]
    [InlineData(0, 300, 100, -200)]
    public void A_cancellation_keeps_the_payments_not_given_back(int debtBefore, int total, int givenBack, int debtAfter) =>
        Assert.Equal((decimal)debtAfter, SalesInvoiceChangeMoney.AfterCancellation(debtBefore, total, givenBack));

    // SALE-07.2 and SALE-08.2: a walk-in invoice only ever brings its kept payments to the profile.
    [Theory]
    [InlineData(0, 300, -300)]
    [InlineData(500, 300, 200)]
    public void A_walk_in_cancellation_brings_only_the_kept_payments(int debtBefore, int kept, int debtAfter) =>
        Assert.Equal((decimal)debtAfter, SalesInvoiceChangeMoney.AfterWalkInCancelled(debtBefore, kept));

    [Fact]
    public void Paid_of_counts_only_payments_received_and_not_cancelled()
    {
        CustomerPayment[] payments =
        [
            new() { Kind = CustomerPaymentKind.Payment, Amount = 1m, StatusId = StatusIds.CustomerPayment.AwaitingConfirmation, Method = CustomerPaymentMethod.Cash },
            new() { Kind = CustomerPaymentKind.Payment, Amount = 2m, StatusId = StatusIds.CustomerPayment.Confirmed, Method = CustomerPaymentMethod.VietQrInvoice },
            new() { Kind = CustomerPaymentKind.Payment, Amount = 4m, StatusId = StatusIds.CustomerPayment.Cancelled, Method = CustomerPaymentMethod.Cash },
            new() { Kind = CustomerPaymentKind.PaidBack, Amount = 8m, StatusId = StatusIds.CustomerPayment.Confirmed, Method = CustomerPaymentMethod.Cash },
        ];

        Assert.Equal(3m, SalesInvoiceChangeMoney.PaidOf(payments));
    }
}
