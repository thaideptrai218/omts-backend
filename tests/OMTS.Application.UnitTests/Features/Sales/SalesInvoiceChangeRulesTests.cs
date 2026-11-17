using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Application.Features.Sales.Invoices.SalesInvoiceAction;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>BR-SALE-10, BR-SALE-11 and BR-CLAIM-03: the endpoints refuse exactly what is not offered; the money of an edit, a price cut and a cancellation (AC-08).</summary>
public class SalesInvoiceChangeRulesTests
{
    private static readonly int[] Statuses = [StatusIds.SalesInvoice.Completed, StatusIds.SalesInvoice.Cancelled];

    // Whatever the role, state and sales return: the endpoints refuse exactly what SALE-09.1 does not offer.
    [Fact]
    public void The_endpoints_refuse_exactly_what_is_not_offered()
    {
        foreach (var status in Statuses)
        {
            foreach (var hasReturn in new[] { true, false })
            {
                foreach (var mayEdit in new[] { true, false })
                {
                    foreach (var mayDelete in new[] { true, false })
                    {
                        var rights = new SalesInvoiceCallerRights(IsCooperativeOwner: mayEdit, MaySell: true, MayCreateReturn: true, MayPrint: true, mayEdit, mayDelete);
                        var offered = SalesInvoiceAllowedActions.Of(new SalesInvoiceActionFacts(status, StatusIds.MessageDelivery.Sent, SaleFinished: true, hasReturn), rights);

                        Assert.Equal(SalesInvoiceAllowedActions.ChangeRefusal(status, hasReturn, mayEdit) is null, offered.Contains(Edit));
                        Assert.Equal(SalesInvoiceAllowedActions.ChangeRefusal(status, hasReturn, mayEdit) is null, offered.Contains(Correct));
                        Assert.Equal(SalesInvoiceAllowedActions.ChangeRefusal(status, hasReturn, mayDelete) is null, offered.Contains(Cancel));
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(false, StatusIds.SalesInvoice.Completed, false, SalesInvoiceChangeRefusal.NotPermitted)]
    [InlineData(true, StatusIds.SalesInvoice.Cancelled, false, SalesInvoiceChangeRefusal.NotCompleted)]
    [InlineData(true, StatusIds.SalesInvoice.Completed, true, SalesInvoiceChangeRefusal.HasSalesReturn)]
    [InlineData(true, StatusIds.SalesInvoice.Completed, false, null)]
    public void The_refusal_names_the_rule(bool permitted, int status, bool hasReturn, SalesInvoiceChangeRefusal? expected) =>
        Assert.Equal(expected, SalesInvoiceAllowedActions.ChangeRefusal(status, hasReturn, permitted));

    // MSG-SALE-20 is what a sales return answers once CLAIM-03 feeds the seam.
    [Fact]
    public void A_sales_return_blocks_both_changes_with_its_own_message()
    {
        Assert.Equal(SalesInvoiceChangeRefusal.HasSalesReturn, SalesInvoiceAllowedActions.ChangeRefusal(StatusIds.SalesInvoice.Completed, hasSalesReturn: true, permitted: true));
        Assert.Equal("MSG-SALE-20", OMTS.Application.Common.Messages.UserMessages.InvoiceHasSalesReturn("HD000001").Code);
    }

    // AC-08: 10,000,000 − 500,000 = 9,500,000 and the debt falls by 500,000 once.
    [Fact]
    public void A_price_cut_moves_the_debt_once_by_its_amount() =>
        Assert.Equal(9_500_000m, SalesInvoiceChangeMoney.AfterCorrection(10_000_000m, 10_000_000m, 9_500_000m));

    [Fact]
    public void Moving_an_invoice_keeps_both_debts_right()
    {
        Assert.Equal(0m, SalesInvoiceChangeMoney.AfterLeaving(200_000m, 300_000m, 100_000m));
        Assert.Equal(200_000m, SalesInvoiceChangeMoney.AfterComingIn(0m, 300_000m, 100_000m));
    }

    // SALE-08 POST-2: the invoice no longer counts; cash given back no longer counts; the rest still does.
    [Fact]
    public void A_cancellation_keeps_every_payment_that_is_not_given_back_in_the_debt()
    {
        Assert.Equal(0m, SalesInvoiceChangeMoney.AfterCancellation(300_000m, 300_000m, 0m));
        Assert.Equal(-300_000m, SalesInvoiceChangeMoney.AfterCancellation(0m, 300_000m, 0m));
        Assert.Equal(0m, SalesInvoiceChangeMoney.AfterCancellation(0m, 300_000m, 300_000m));
        Assert.Equal(-300_000m, SalesInvoiceChangeMoney.AfterWalkInCancelled(0m, 300_000m));
    }

    [Fact]
    public void Paid_of_an_invoice_counts_payments_that_are_not_cancelled()
    {
        CustomerPayment[] payments =
        [
            new() { Kind = CustomerPaymentKind.Payment, Amount = 100m, StatusId = StatusIds.CustomerPayment.AwaitingConfirmation },
            new() { Kind = CustomerPaymentKind.Payment, Amount = 50m, StatusId = StatusIds.CustomerPayment.Completed },
            new() { Kind = CustomerPaymentKind.Payment, Amount = 25m, StatusId = StatusIds.CustomerPayment.Cancelled },
            new() { Kind = CustomerPaymentKind.PaidBack, Amount = 7m, StatusId = StatusIds.CustomerPayment.Completed },
        ];

        Assert.Equal(150m, SalesInvoiceChangeMoney.PaidOf(payments));
    }
}
