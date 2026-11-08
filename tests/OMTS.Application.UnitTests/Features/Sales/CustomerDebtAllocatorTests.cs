using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>BR-DEBT-05: own invoice first, then the oldest invoices; money paid back, cancelled rows, ties and the invariant Σ unpaid = debt.</summary>
public class CustomerDebtAllocatorTests
{
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 11, 8);

    internal static CustomerLedgerInvoice Invoice(string code, int daysAgo, decimal total, bool cancelled = false, int minute = 0) => new(
        Customer, Guid.NewGuid(), code, new DateTimeOffset(Today.AddDays(-daysAgo).ToDateTime(new TimeOnly(8, minute)), TimeSpan.FromHours(7)),
        Today.AddDays(-daysAgo), total, cancelled);

    internal static CustomerLedgerPayment Payment(
        decimal amount, CustomerLedgerInvoice? invoice = null, int daysAgo = 0, CustomerPaymentKind kind = CustomerPaymentKind.Payment,
        int statusId = StatusIds.CustomerPayment.Confirmed) => new(
        Customer, Guid.NewGuid(), "PT" + Guid.NewGuid().ToString("N")[..6], kind, Today.AddDays(-daysAgo), amount, CustomerPaymentMethod.Cash, invoice?.Id, invoice?.Code, statusId);

    private static CustomerLedgerPayment PaidBack(decimal amount, int daysAgo = 0) => Payment(amount, daysAgo: daysAgo, kind: CustomerPaymentKind.PaidBack);

    private static CustomerDebtAllocation Allocate(CustomerLedgerInvoice[] invoices, params CustomerLedgerPayment[] payments) =>
        CustomerDebtAllocator.Allocate(invoices, payments, Today);

    private static List<(string Code, decimal Unpaid)> Unpaid(CustomerDebtAllocation allocation) => allocation.UnpaidInvoices.Select(i => (i.Code, i.Unpaid)).ToList();

    [Fact]
    public void Other_payments_cover_the_oldest_invoices_first_and_the_age_counts_from_the_oldest_unpaid()
    {
        var a = Invoice("HD000001", 10, 1_000_000m);
        var b = Invoice("HD000002", 5, 2_000_000m);
        var c = Invoice("HD000003", 1, 3_000_000m);

        var allocation = Allocate([c, a, b], Payment(2_500_000m));

        Assert.Equal([("HD000002", 500_000m), ("HD000003", 3_000_000m)], Unpaid(allocation));
        Assert.Equal((3_500_000m, Today.AddDays(-5), 5), (allocation.Debt, allocation.OldestUnpaidDate, allocation.DebtAgeDays));
        Assert.Equal(1_500_000m, allocation.UnpaidInvoices[0].Covered);
        Assert.Equal(0m, allocation.UnpaidOf(a.Id));
    }

    [Fact]
    public void A_payment_with_an_invoice_covers_that_invoice_first_and_its_leftover_covers_the_oldest()
    {
        var a = Invoice("HD000001", 10, 1_000_000m);
        var b = Invoice("HD000002", 5, 2_000_000m);
        var c = Invoice("HD000003", 1, 3_000_000m);

        // Cash at the port with C, and a transfer not tied to an invoice: C keeps its own 1,000,000, the transfer pays A.
        Assert.Equal([("HD000002", 2_000_000m), ("HD000003", 2_000_000m)], Unpaid(Allocate([a, b, c], Payment(1_000_000m, c), Payment(1_000_000m))));

        // MSG-SALE-11: cash above the total of C subtracted from the debt; what C does not need pays A, then part of B.
        Assert.Equal([("HD000002", 1_500_000m)], Unpaid(Allocate([a, b, c], Payment(4_500_000m, c))));

        // An invoice paid in cash and VietQR (two payments) needs no more.
        Assert.Equal([("HD000001", 1_000_000m), ("HD000002", 2_000_000m)], Unpaid(Allocate([a, b, c], Payment(1_000_000m, c), Payment(2_000_000m, c))));
    }

    [Fact]
    public void A_payment_above_the_debt_leaves_no_invoice_unpaid_and_the_difference_is_money_owed_back()
    {
        var allocation = Allocate([Invoice("HD000001", 3, 1_000_000m), Invoice("HD000002", 2, 2_000_000m)], Payment(3_400_000m));

        Assert.Empty(allocation.UnpaidInvoices);
        Assert.Equal((-400_000m, (DateOnly?)null, (int?)null), (allocation.Debt, allocation.OldestUnpaidDate, allocation.DebtAgeDays));

        // Paid back: the debt is 0 and still nothing unpaid.
        var paidBack = Allocate([Invoice("HD000001", 3, 1_000_000m), Invoice("HD000002", 2, 2_000_000m)], Payment(3_400_000m), PaidBack(400_000m));
        Assert.Equal((0m, 0), (paidBack.Debt, paidBack.UnpaidInvoices.Count));
    }

    [Fact]
    public void A_payment_cancelled_after_money_was_paid_back_makes_the_newest_covered_invoices_unpaid_again()
    {
        var a = Invoice("HD000001", 10, 1_000_000m);
        var b = Invoice("HD000002", 5, 2_000_000m);
        var cancelled = Payment(3_000_000m, statusId: StatusIds.CustomerPayment.Cancelled);

        // A paid in cash, B by the transfer, 1,000,000 owed back and paid back; then the transfer is cancelled: debt 3,000,000, both invoices unpaid.
        var allocation = Allocate([a, b], Payment(1_000_000m, a), cancelled, PaidBack(1_000_000m));

        Assert.Equal([("HD000001", 1_000_000m), ("HD000002", 2_000_000m)], Unpaid(allocation));
        Assert.Equal(3_000_000m, allocation.Debt);

        // Both paid at the port, 500,000 transferred on top and paid back, then that transfer cancelled: the pool is 500,000 short, and the newest covered invoice gives it back.
        var both = Allocate([a, b], Payment(1_000_000m, a), Payment(2_000_000m, b), Payment(500_000m, statusId: StatusIds.CustomerPayment.Cancelled), PaidBack(500_000m));
        Assert.Equal([("HD000002", 500_000m)], Unpaid(both));
        Assert.Equal(500_000m, both.Debt);

        // The cancelled transfer recorded again for 2,000,000 instead: what is left after the money paid back still covers B in part.
        var again = Allocate([a, b], Payment(1_000_000m, a), Payment(2_000_000m), PaidBack(500_000m));
        Assert.Equal([("HD000002", 500_000m)], Unpaid(again));
        Assert.Equal(500_000m, again.Debt);
    }

    [Fact]
    public void Invoices_of_the_same_time_are_covered_by_code_and_cancelled_rows_count_nowhere()
    {
        var second = Invoice("HD000002", 2, 1_000_000m);
        var first = Invoice("HD000001", 2, 1_000_000m);
        var cancelledInvoice = Invoice("HD000000", 9, 5_000_000m, cancelled: true);

        // Same invoice time: HD000001 first. The cash that came with the cancelled invoice is kept (BR-SALE-11) and covers the oldest invoice instead.
        var allocation = Allocate([second, cancelledInvoice, first], Payment(1_000_000m, cancelledInvoice), Payment(700_000m, statusId: StatusIds.CustomerPayment.Cancelled));

        Assert.Equal([("HD000002", 1_000_000m)], Unpaid(allocation));
        Assert.Equal(1_000_000m, allocation.Debt);
        Assert.Equal(0m, allocation.UnpaidOf(cancelledInvoice.Id));

        // Within a day the invoice time decides before the code.
        var later = Invoice("HD000001", 1, 1_000_000m, minute: 30);
        var earlier = Invoice("HD000009", 1, 1_000_000m, minute: 5);
        Assert.Equal([("HD000001", 1_000_000m)], Unpaid(Allocate([later, earlier], Payment(1_000_000m))));
    }

    [Fact]
    public void Awaiting_payments_count_and_a_debt_without_any_invoice_is_as_old_as_the_money_paid_back()
    {
        var awaiting = Allocate([Invoice("HD000001", 4, 1_000_000m)], Payment(1_000_000m, statusId: StatusIds.CustomerPayment.AwaitingConfirmation));
        Assert.Equal((0m, 0), (awaiting.Debt, awaiting.UnpaidInvoices.Count));

        var noInvoice = Allocate([], Payment(200_000m, statusId: StatusIds.CustomerPayment.Cancelled, daysAgo: 6), PaidBack(200_000m, daysAgo: 3));
        Assert.Equal((200_000m, 3), (noInvoice.Debt, noInvoice.DebtAgeDays));
        Assert.Empty(noInvoice.UnpaidInvoices);
        Assert.Equal((0m, (int?)null), (Allocate([]).Debt, Allocate([]).DebtAgeDays));
    }

    // The amounts still unpaid add up to the debt when it is above 0, and nothing is unpaid when it is 0 or below, for any mix of rows the rules allow
    // (money paid back never above what was paid). Seeded, so the same vectors every run.
    [Fact]
    public void The_unpaid_amounts_always_add_up_to_the_debt()
    {
        var random = new Random(8_224);
        for (var round = 0; round < 500; round++)
        {
            var invoices = Enumerable.Range(1, random.Next(0, 6))
                .Select(n => Invoice($"HD{n:D6}", random.Next(0, 40), random.Next(1, 50) * 100_000m, cancelled: random.Next(5) == 0, minute: random.Next(0, 59)))
                .ToArray();
            var payments = Enumerable.Range(0, random.Next(0, 6))
                .Select(_ => Payment(
                    random.Next(1, 40) * 100_000m,
                    invoices.Length > 0 && random.Next(2) == 0 ? invoices[random.Next(invoices.Length)] : null,
                    statusId: random.Next(6) == 0 ? StatusIds.CustomerPayment.Cancelled : StatusIds.CustomerPayment.Confirmed))
                .ToList();
            var received = payments.Where(p => !p.Cancelled).Sum(p => p.Amount);
            if (received > 0 && random.Next(3) == 0)
            {
                payments.Add(PaidBack(Math.Max(1m, Math.Floor(received * (decimal)random.NextDouble()))));
            }

            var allocation = Allocate(invoices, [.. payments]);

            Assert.Equal(Math.Max(0m, allocation.Debt), allocation.UnpaidInvoices.Sum(i => i.Unpaid));
            Assert.All(allocation.UnpaidInvoices, i => Assert.InRange(i.Unpaid, 1m, i.Total));
            Assert.Equal(allocation.Debt > 0, allocation.DebtAgeDays is not null);
        }
    }
}
