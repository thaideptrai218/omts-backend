using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Application.UnitTests.Features.Sales.CustomerDebtAllocatorTests;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>QA of BR-DEBT-05 and BR-DEBT-04 (PR 8a): vectors the developer's tests leave out, and the statement boundaries.</summary>
public class CustomerDebtAllocatorQaTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);

    private static CustomerLedgerPayment PaidBack(decimal amount, int daysAgo = 0) => Payment(amount, daysAgo: daysAgo, kind: CustomerPaymentKind.PaidBack);

    private static CustomerLedgerPayment Cancelled(decimal amount, int daysAgo = 0) => Payment(amount, daysAgo: daysAgo, statusId: StatusIds.CustomerPayment.Cancelled);

    private static List<(string Code, decimal Unpaid)> Unpaid(CustomerDebtAllocation allocation) => allocation.UnpaidInvoices.Select(i => (i.Code, i.Unpaid)).ToList();

    // BR-DEBT-05 "The amounts still unpaid always add up to the customer debt when it is above zero." A transfer of 1,500,000 pays A (1,000,000), 500,000 is owed back and
    // paid back (BR-DEBT-03: up to the money owed back), then the transfer is cancelled (its money never arrived). Debt = 1,000,000 − 0 + 500,000 = 1,500,000.
    [Fact]
    public void The_unpaid_amounts_add_up_to_the_debt_after_a_transfer_partly_paid_back_is_cancelled()
    {
        var a = Invoice("HD000001", 10, 1_000_000m);

        var allocation = CustomerDebtAllocator.Allocate([a], [Cancelled(1_500_000m, 8), PaidBack(500_000m, 6)], Today);

        Assert.Equal(1_500_000m, allocation.Debt);
        Assert.Equal(allocation.Debt, allocation.UnpaidInvoices.Sum(i => i.Unpaid));
    }

    // The rows come from two SQL queries with no ORDER BY: the answer must not depend on the order they arrive in.
    [Fact]
    public void The_allocation_does_not_depend_on_the_order_the_rows_come_in()
    {
        var random = new Random(26_1108);
        for (var round = 0; round < 300; round++)
        {
            var invoices = Enumerable.Range(1, random.Next(1, 7))
                .Select(n => Invoice($"HD{n:D6}", random.Next(0, 30), random.Next(1, 40) * 100_000m, cancelled: random.Next(6) == 0, minute: random.Next(0, 3)))
                .ToArray();
            var payments = Enumerable.Range(0, random.Next(0, 7))
                .Select(_ => Payment(
                    random.Next(1, 30) * 100_000m,
                    random.Next(2) == 0 ? invoices[random.Next(invoices.Length)] : null,
                    statusId: random.Next(5) switch
                    {
                        0 => StatusIds.CustomerPayment.Cancelled,
                        1 => StatusIds.CustomerPayment.AwaitingConfirmation,
                        _ => StatusIds.CustomerPayment.Confirmed,
                    }))
                .ToList();
            var received = payments.Where(p => !p.Cancelled).Sum(p => p.Amount);
            if (received > 0 && random.Next(3) == 0)
            {
                payments.Add(PaidBack(Math.Max(1m, Math.Floor(received * (decimal)random.NextDouble() / 1000m) * 1000m)));
            }

            var expected = CustomerDebtAllocator.Allocate(invoices, payments, Today);
            var shuffled = CustomerDebtAllocator.Allocate(invoices.OrderBy(_ => random.Next()), payments.OrderBy(_ => random.Next()), Today);

            Assert.Equal(expected.Debt, shuffled.Debt);
            Assert.Equal(expected.UnpaidInvoices, shuffled.UnpaidInvoices);
            Assert.Equal(expected.DebtAgeDays, shuffled.DebtAgeDays);
        }
    }

    // BR-DEBT-02 "When a payment counts": from the time it is recorded, also while Awaiting confirmation; BR-DEBT-05 (2): cash at the port covers its own invoice first.
    [Fact]
    public void Cash_awaiting_confirmation_covers_its_own_invoice_before_an_older_one()
    {
        var older = Invoice("HD000001", 9, 1_000_000m);
        var newer = Invoice("HD000002", 1, 1_000_000m);

        var allocation = CustomerDebtAllocator.Allocate(
            [older, newer], [Payment(1_000_000m, newer, daysAgo: 1, statusId: StatusIds.CustomerPayment.AwaitingConfirmation)], Today);

        Assert.Equal([("HD000001", 1_000_000m)], Unpaid(allocation));
        Assert.Equal((1_000_000m, 9), (allocation.Debt, allocation.DebtAgeDays));
        Assert.Equal(0m, allocation.UnpaidOf(newer.Id));
    }

    // BR-DEBT-02 Money owed back left for the next invoices; BR-DEBT-05 (5): money paid back is taken from the money no invoice needs, never from a later invoice.
    [Fact]
    public void Money_owed_back_pays_the_next_invoice_and_money_paid_back_never_covers_one()
    {
        var a = Invoice("HD000001", 20, 1_000_000m);
        var c = Invoice("HD000003", 2, 1_000_000m);

        var left = CustomerDebtAllocator.Allocate([a, c], [Payment(1_200_000m, daysAgo: 15)], Today);
        Assert.Equal([("HD000003", 800_000m)], Unpaid(left));
        Assert.Equal((800_000m, 2), (left.Debt, left.DebtAgeDays));

        var paidBack = CustomerDebtAllocator.Allocate([a, c], [Payment(1_500_000m, daysAgo: 15), PaidBack(500_000m, 14)], Today);
        Assert.Equal([("HD000003", 1_000_000m)], Unpaid(paidBack));
        Assert.Equal(1_000_000m, paidBack.Debt);
    }

    // BR-DEBT-05 (3): the part of a port payment that its invoice no longer needs (here a cancelled invoice, BR-SALE-11 keeps the payment) covers the oldest invoices.
    [Fact]
    public void A_kept_payment_of_a_cancelled_invoice_covers_the_oldest_invoice_and_the_age_moves_to_the_next()
    {
        var oldest = Invoice("HD000001", 30, 400_000m);
        var next = Invoice("HD000002", 12, 600_000m);
        var cancelled = Invoice("HD000003", 3, 900_000m, cancelled: true);

        var allocation = CustomerDebtAllocator.Allocate([oldest, next, cancelled], [Payment(900_000m, cancelled, daysAgo: 3)], Today);

        Assert.Equal([("HD000002", 100_000m)], Unpaid(allocation));
        Assert.Equal((100_000m, Today.AddDays(-12), 12), (allocation.Debt, allocation.OldestUnpaidDate, allocation.DebtAgeDays));
    }

    // BR-DEBT-04: the period includes its first and last day; what is dated before it is in the opening debt; after it, in nothing shown.
    [Fact]
    public void The_statement_includes_its_first_and_last_day_and_opens_with_what_came_before()
    {
        var before = Invoice("HD000001", 11, 500_000m);
        var onFirst = Invoice("HD000002", 10, 300_000m);
        var onLast = Invoice("HD000003", 5, 200_000m);
        var after = Invoice("HD000004", 4, 700_000m);

        var lines = CustomerStatementLedger.Build([before, onFirst, onLast, after], [Payment(100_000m, daysAgo: 10)], Today.AddDays(-10), Today.AddDays(-5));

        Assert.Equal((500_000m, 900_000m), (lines.OpeningDebt, lines.ClosingDebt));
        Assert.Equal(["HD000002", "HD000003"], lines.Entries.Where(e => e.Kind == CustomerStatementEntryKind.Invoice).Select(e => e.Code).ToList());
        Assert.Equal(3, lines.Entries.Count);
    }

    // BR-DEBT-04 "in date order with the debt after each": on one day the invoice comes before the cash paid with it, so the debt never reads below the opening.
    [Fact]
    public void On_one_day_the_invoice_comes_before_the_cash_paid_with_it()
    {
        var invoice = Invoice("HD000009", 1, 600_000m);
        var lines = CustomerStatementLedger.Build([invoice], [Payment(600_000m, invoice, daysAgo: 1)], Today.AddDays(-1), Today);

        Assert.Equal(
            [(CustomerStatementEntryKind.Invoice, 600_000m), (CustomerStatementEntryKind.Payment, 0m)],
            lines.Entries.Select(e => (e.Kind, e.DebtAfter)).ToList());
    }

    // A period after every entry: nothing listed, opening = closing = the debt.
    [Fact]
    public void A_period_after_every_entry_opens_and_closes_at_the_debt_with_no_entries()
    {
        var lines = CustomerStatementLedger.Build([Invoice("HD000001", 40, 800_000m)], [Payment(300_000m, daysAgo: 35)], Today.AddDays(-3), Today);

        Assert.Empty(lines.Entries);
        Assert.Equal((500_000m, 500_000m), (lines.OpeningDebt, lines.ClosingDebt));
    }
}
