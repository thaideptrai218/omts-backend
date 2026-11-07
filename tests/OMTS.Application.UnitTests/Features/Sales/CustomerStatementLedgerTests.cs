using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Constants;
using static OMTS.Application.UnitTests.Features.Sales.CustomerDebtAllocatorTests;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>BR-DEBT-04: debt at the start, entries of the period in date order with the debt after each, debt at the end; cancelled entries listed and counted 0.</summary>
public class CustomerStatementLedgerTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);

    [Fact]
    public void The_opening_debt_counts_what_came_before_and_each_entry_carries_the_running_debt_to_the_closing_debt()
    {
        var before = Invoice("HD000001", 20, 1_000_000m);
        var inPeriod = Invoice("HD000002", 5, 2_000_000m);
        var after = Invoice("HD000003", 0, 9_000_000m);
        var cash = Payment(500_000m, inPeriod, daysAgo: 5, statusId: StatusIds.CustomerPayment.AwaitingConfirmation);
        var transfer = Payment(300_000m, daysAgo: 15);

        var lines = CustomerStatementLedger.Build([after, inPeriod, before], [cash, transfer, Payment(100_000m, daysAgo: 25)], Today.AddDays(-15), Today.AddDays(-1));

        Assert.Equal(900_000m, lines.OpeningDebt);
        Assert.Equal(
            [
                (CustomerStatementEntryKind.Payment, -300_000m, 600_000m),
                (CustomerStatementEntryKind.Invoice, 2_000_000m, 2_600_000m),
                (CustomerStatementEntryKind.Payment, -500_000m, 2_100_000m),
            ],
            lines.Entries.Select(e => (e.Kind, e.DebtChange, e.DebtAfter)).ToList());
        Assert.Equal(2_100_000m, lines.ClosingDebt);

        // Same day: the invoice before the cash that came with it, which names it and is still Awaiting confirmation.
        var payment = lines.Entries[2];
        Assert.Equal(("HD000002", true, 500_000m), (payment.InvoiceCode, payment.AwaitingConfirmation, payment.Amount));
    }

    [Fact]
    public void A_cancelled_invoice_or_payment_stays_on_its_date_marked_cancelled_and_counts_zero()
    {
        var cancelledInvoice = Invoice("HD000001", 4, 1_000_000m, cancelled: true);
        var kept = Invoice("HD000002", 3, 2_000_000m);
        var cancelledPayment = Payment(700_000m, daysAgo: 2, statusId: StatusIds.CustomerPayment.Cancelled);

        var lines = CustomerStatementLedger.Build([cancelledInvoice, kept], [cancelledPayment], Today.AddDays(-7), Today);

        Assert.Equal(0m, lines.OpeningDebt);
        Assert.Equal(
            [("HD000001", true, 1_000_000m, 0m, 0m), ("HD000002", false, 2_000_000m, 2_000_000m, 2_000_000m), (cancelledPayment.Code, true, 700_000m, 0m, 2_000_000m)],
            lines.Entries.Select(e => (e.Code, e.Cancelled, e.Amount, e.DebtChange, e.DebtAfter)).ToList());
        Assert.Equal(2_000_000m, lines.ClosingDebt);

        // The closing debt of a period that ends today is the debt of the allocation over the same rows.
        Assert.Equal(CustomerDebtAllocator.Allocate([cancelledInvoice, kept], [cancelledPayment], Today).Debt, lines.ClosingDebt);
    }

    [Fact]
    public void Money_paid_back_raises_the_debt_and_an_empty_period_closes_at_its_opening_debt()
    {
        var invoice = Invoice("HD000001", 30, 1_000_000m);
        var payments = new[] { Payment(1_500_000m, daysAgo: 20), Payment(500_000m, daysAgo: 10, kind: OMTS.Domain.Enums.CustomerPaymentKind.PaidBack) };

        var month = CustomerStatementLedger.Build([invoice], payments, Today.AddDays(-30), Today);
        Assert.Equal([1_000_000m, -500_000m, 0m], month.Entries.Select(e => e.DebtAfter).ToList());
        Assert.Equal(CustomerStatementEntryKind.PaidBack, month.Entries[^1].Kind);

        var empty = CustomerStatementLedger.Build([invoice], payments, Today.AddDays(-5), Today);
        Assert.Equal((0m, 0, 0m), (empty.OpeningDebt, empty.Entries.Count, empty.ClosingDebt));
    }
}
