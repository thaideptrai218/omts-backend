using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Domain.Enums;
using Kind = OMTS.Application.Features.Purchasing.Payments.SupplierStatementEntryKind;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>BR-PURCHASE-04: the debt formula, FIFO coverage of the unpaid receipts, advances, money paid back and the statement.</summary>
public class SupplierDebtLedgerTests
{
    private static readonly DateOnly Today = new(2026, 11, 4);

    private static DateOnly Ago(int days) => Today.AddDays(-days);

    private static DebtReceiptRow Receipt(string code, int daysAgo, decimal total, decimal returned = 0) =>
        new(Guid.Empty, Guid.NewGuid(), code, Ago(daysAgo), total, returned);

    private static StatementPaymentRow Payment(string code, int daysAgo, decimal amount, SupplierPaymentKind kind = SupplierPaymentKind.Payment) =>
        new(Guid.NewGuid(), code, kind, Ago(daysAgo), amount, PaymentMethod.Cash, null, null);

    [Fact]
    public void The_debt_is_receipts_minus_returns_minus_payments_plus_money_paid_back_and_refunds()
    {
        var sums = new SupplierDebtSums(Receipts: 1_000, Returned: 100, Refunded: 20, Paid: 300, PaidBack: 50);

        Assert.Equal(1_000 - 100 + 20 - 300 + 50, sums.Debt);
        Assert.Equal(300 - 50 - 20, sums.Credit);
        Assert.Equal(sums.Receipts - sums.Returned - sums.Credit, sums.Debt);
    }

    [Fact]
    public void Payments_cover_the_oldest_receipts_first_whatever_order_the_receipts_come_in()
    {
        var newest = Receipt("PN000003", 1, 500);
        var middle = Receipt("PN000002", 5, 300);
        var oldest = Receipt("PN000001", 10, 200);

        var unpaid = SupplierDebtLedger.UnpaidReceipts([newest, oldest, middle], credit: 350, Today);

        // 200 pays the oldest in full, 150 of the 300 pays the next: the oldest is not unpaid any more.
        Assert.Equal(["PN000002", "PN000003"], unpaid.Select(r => r.Code));
        Assert.Equal((150m, 150m, 5), (unpaid[0].Covered, unpaid[0].Remaining, unpaid[0].AgeDays));
        Assert.Equal((0m, 500m, 1), (unpaid[1].Covered, unpaid[1].Remaining, unpaid[1].AgeDays));
    }

    [Fact]
    public void A_return_lowers_its_own_receipt_before_any_payment_covers_it()
    {
        var unpaid = SupplierDebtLedger.UnpaidReceipts([Receipt("PN000001", 9, 300, returned: 300), Receipt("PN000002", 3, 400, returned: 100)], credit: 100, Today);

        var row = Assert.Single(unpaid);
        Assert.Equal(("PN000002", 400m, 100m, 100m, 200m), (row.Code, row.Total, row.Returned, row.Covered, row.Remaining));
    }

    [Fact]
    public void An_advance_covers_later_receipts_and_a_debt_below_zero_leaves_nothing_unpaid()
    {
        // BR-PURCHASE-08: the next receipts use up what was paid in advance, whatever the dates.
        var advance = new SupplierDebtSums(Receipts: 400, Returned: 0, Refunded: 0, Paid: 500, PaidBack: 0);

        Assert.Equal(-100m, advance.Debt);
        Assert.Empty(SupplierDebtLedger.UnpaidReceipts([Receipt("PN000001", 1, 400)], advance.Credit, Today));
    }

    [Fact]
    public void Money_paid_back_above_the_payments_covers_nothing_and_the_excess_stays_on_the_debt()
    {
        var sums = new SupplierDebtSums(Receipts: 400, Returned: 0, Refunded: 0, Paid: 100, PaidBack: 150);
        var unpaid = SupplierDebtLedger.UnpaidReceipts([Receipt("PN000001", 7, 400)], sums.Credit, Today);

        var row = Assert.Single(unpaid);
        Assert.Equal((0m, 400m), (row.Covered, row.Remaining));
        Assert.Equal(450m, sums.Debt);
    }

    [Fact]
    public void A_return_value_above_its_receipt_until_P4_counts_as_credit_for_the_next_receipt()
    {
        // 1,500 rounds to a return of 2,000 until P4: the 500 over is credit, as the debt formula says.
        var unpaid = SupplierDebtLedger.UnpaidReceipts([Receipt("PN000001", 9, 1_500, returned: 2_000), Receipt("PN000002", 3, 800)], credit: 0, Today);

        var row = Assert.Single(unpaid);
        Assert.Equal(("PN000002", 500m, 300m), (row.Code, row.Covered, row.Remaining));
    }

    public static TheoryData<decimal, int?> MoneyBackVectors => new()
    {
        { 0m, 20 }, // nothing paid: the oldest amount paid back is the first not covered
        { 99m, 20 },
        { 100m, 10 }, // the first 100 is covered, the age moves to the next
        { 299m, 10 },
        { 300m, null }, // all covered, so no debt comes from them
    };

    [Theory]
    [MemberData(nameof(MoneyBackVectors))]
    public void With_no_unpaid_receipt_the_age_counts_from_the_oldest_money_paid_back_that_no_payment_covers(decimal paid, int? daysAgo)
    {
        var moneyBack = new[] { new DebtMoneyBackRow(Guid.Empty, Ago(10), 200), new DebtMoneyBackRow(Guid.Empty, Ago(20), 100) };

        Assert.Equal(daysAgo is { } days ? Ago(days) : null, SupplierDebtLedger.OldestUncoveredMoneyBack(moneyBack, paid));
    }

    [Fact]
    public void The_statement_is_in_date_then_kind_then_code_order_with_the_debt_after_each_and_opening_plus_entries_is_closing()
    {
        const decimal opening = 1_000;
        var entries = SupplierDebtLedger.Statement(
            opening,
            [Receipt("PN000002", 2, 500), Receipt("PN000001", 2, 300), Receipt("PN000003", 9, 100)],
            [new StatementReturnRow(Guid.NewGuid(), "PRT-1", "PN000001", Ago(2), 200, 50)],
            [Payment("TT000002", 2, 70), Payment("TT000003", 2, 30, SupplierPaymentKind.PaidBack), Payment("TT000001", 9, 40)]);

        Assert.Equal(
            [
                (Kind.Receipt, "PN000003", 100m, 1_100m), (Kind.Payment, "TT000001", -40m, 1_060m),
                (Kind.Receipt, "PN000001", 300m, 1_360m), (Kind.Receipt, "PN000002", 500m, 1_860m),
                (Kind.Return, "PRT-1", -200m, 1_660m), (Kind.ReturnRefund, "PRT-1", 50m, 1_710m),
                (Kind.Payment, "TT000002", -70m, 1_640m), (Kind.PaidBack, "TT000003", 30m, 1_670m),
            ],
            entries.Select(e => (e.Kind, e.Code, e.Amount, e.DebtAfter)).ToList());
        Assert.Equal(opening + entries.Sum(e => e.Amount), entries[^1].DebtAfter);
        Assert.Equal("PN000001", entries.Single(e => e.Kind == Kind.Return).ReceiptCode);
    }

    [Fact]
    public void A_return_with_no_refund_has_no_refund_entry_and_a_period_with_nothing_has_no_entry()
    {
        var entries = SupplierDebtLedger.Statement(0, [], [new StatementReturnRow(Guid.NewGuid(), "PRT-1", "PN000001", Ago(1), 200, 0)], []);

        Assert.Equal((Kind.Return, -200m), (Assert.Single(entries).Kind, entries[0].DebtAfter));
        Assert.Empty(SupplierDebtLedger.Statement(500, [], [], []));
    }
}
