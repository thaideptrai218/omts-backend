using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Payments;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>Payments that name invoices, discounts, adjustments and payments waiting for their transfer in the debt reading, the statement and the transfer rules.</summary>
public class CustomerPaymentAllocationTests
{
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 11, 10);
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(7));

    private static CustomerLedgerInvoice Invoice(string code, int day, decimal total) =>
        new(Customer, Guid.NewGuid(), code, Start.AddDays(day), DateOnly.FromDateTime(Start.AddDays(day).DateTime), total, false);

    private static CustomerLedgerPayment Payment(
        string code, decimal amount, int day, decimal discount = 0m, int statusId = StatusIds.CustomerPayment.Completed, params (Guid InvoiceId, decimal Amount)[] parts) =>
        new(Customer, Guid.NewGuid(), code, CustomerPaymentKind.Payment, DateOnly.FromDateTime(Start.AddDays(day).DateTime), amount, CustomerPaymentMethod.Cash, null, null, statusId,
            PaidAt: Start.AddDays(day).AddHours(1), DiscountAmount: discount, Allocations: parts.Select(p => new CustomerLedgerAllocation(p.InvoiceId, p.Amount)).ToList());

    [Fact]
    public void A_payment_covers_the_invoice_it_names_before_the_oldest_and_the_rest_the_oldest()
    {
        var older = Invoice("HD1", 0, 300_000m);
        var newer = Invoice("HD2", 1, 600_000m);

        var allocation = CustomerDebtAllocator.Allocate([older, newer], [Payment("PT1", 400_000m, 2, parts: (newer.Id, 100_000m))], Today);

        // 100,000 on the newer invoice as named, 300,000 more of the payment goes to the oldest invoice.
        Assert.Equal(500_000m, allocation.Debt);
        Assert.Equal([("HD2", 500_000m)], allocation.UnpaidInvoices.Select(u => (u.Code, u.Unpaid)));
    }

    [Fact]
    public void A_part_above_what_the_invoice_needs_and_a_discount_cover_like_money_and_an_old_payment_with_no_part_reads_as_before()
    {
        var older = Invoice("HD1", 0, 300_000m);
        var newer = Invoice("HD2", 1, 600_000m);
        var discount = Payment("PT1", 0m, 2, discount: 100_000m, parts: (older.Id, 100_000m));
        var legacy = Payment("PT2", 150_000m, 3);

        var allocation = CustomerDebtAllocator.Allocate([older, newer], [discount, legacy], Today);

        Assert.Equal(650_000m, allocation.Debt);
        Assert.Equal([("HD1", 50_000m), ("HD2", 600_000m)], allocation.UnpaidInvoices.Select(u => (u.Code, u.Unpaid)));
    }

    [Fact]
    public void A_payment_waiting_for_its_transfer_and_a_cancelled_one_count_nowhere()
    {
        var invoice = Invoice("HD1", 0, 300_000m);
        var pending = Payment("PT1", 200_000m, 1, statusId: StatusIds.CustomerPayment.PendingTransfer, parts: (invoice.Id, 200_000m));
        var cancelled = Payment("PT2", 100_000m, 1, statusId: StatusIds.CustomerPayment.Cancelled);

        var allocation = CustomerDebtAllocator.Allocate([invoice], [pending, cancelled], Today);

        Assert.Equal((300_000m, 300_000m), (allocation.Debt, allocation.UnpaidInvoices.Single().Unpaid));
    }

    [Fact]
    public void An_adjustment_that_raises_the_debt_uncovers_the_newest_invoices_and_one_that_lowers_it_covers_the_oldest()
    {
        var older = Invoice("HD1", 0, 300_000m);
        var newer = Invoice("HD2", 1, 600_000m);
        var paid = Payment("PT1", 900_000m, 2);
        CustomerLedgerAdjustment Make(decimal amount, bool cancelled = false) => new(Customer, Guid.NewGuid(), "DC1", Today, Start, amount, "Why", cancelled);

        var up = CustomerDebtAllocator.Allocate([older, newer], [paid], Today, [Make(100_000m)]);
        Assert.Equal(100_000m, up.Debt);
        Assert.Equal([("HD2", 100_000m)], up.UnpaidInvoices.Select(u => (u.Code, u.Unpaid)));
        var down = CustomerDebtAllocator.Allocate([older, newer], [], Today, [Make(-400_000m)]);
        Assert.Equal(500_000m, down.Debt);
        Assert.Equal([("HD2", 500_000m)], down.UnpaidInvoices.Select(u => (u.Code, u.Unpaid)));
        var ignored = CustomerDebtAllocator.Allocate([older, newer], [], Today, [Make(-400_000m, cancelled: true)]);
        Assert.Equal(900_000m, ignored.Debt);
    }

    [Fact]
    public void The_statement_has_an_entry_for_the_money_the_discount_a_price_cut_and_an_adjustment_each_with_the_debt_after_it()
    {
        var invoice = Invoice("HD1", 0, 270_000m) with { PriceCuts = [new CustomerLedgerPriceCut(Guid.NewGuid(), new DateOnly(2026, 11, 2), 30_000m, "Claim")] };
        var payment = Payment("PT1", 100_000m, 3, discount: 20_000m);
        var pending = Payment("PT2", 50_000m, 4, statusId: StatusIds.CustomerPayment.PendingTransfer);
        var adjustment = new CustomerLedgerAdjustment(Customer, Guid.NewGuid(), "DC1", new DateOnly(2026, 11, 5), Start.AddDays(4), 10_000m, "Fee", false);

        var lines = CustomerStatementLedger.Build([invoice], [payment, pending], new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), [adjustment]);

        Assert.Equal(
            [
                (CustomerStatementEntryKind.Invoice, 300_000m), (CustomerStatementEntryKind.PriceCut, 270_000m), (CustomerStatementEntryKind.Payment, 170_000m),
                (CustomerStatementEntryKind.Discount, 150_000m), (CustomerStatementEntryKind.Payment, 150_000m), (CustomerStatementEntryKind.Adjustment, 160_000m),
            ],
            lines.Entries.Select(e => (e.Kind, e.DebtAfter)));
        Assert.True(lines.Entries.Single(e => e.Code == "PT2").Pending);
        Assert.Equal(160_000m, lines.ClosingDebt);
    }

    [Fact]
    public void A_short_transfer_cuts_the_newest_invoice_first_and_an_enough_one_cuts_nothing()
    {
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        IReadOnlyList<(Guid, DateTimeOffset, string, decimal)> parts = [(older, Start, "HD1", 300_000m), (newer, Start.AddDays(1), "HD2", 200_000m)];

        Assert.Equal([(older, 300_000m), (newer, 50_000m)], CustomerPaymentRules.TrimParts(parts, 350_000m));
        Assert.Equal([(older, 100_000m)], CustomerPaymentRules.TrimParts(parts, 100_000m));
        Assert.Equal([(older, 300_000m), (newer, 200_000m)], CustomerPaymentRules.TrimParts(parts, 700_000m));
        Assert.Equal("Khách chuyển 800.000, phiếu 1.000.000", CustomerPaymentRules.TransferDifferenceNote(800_000m, 1_000_000m));
    }

    [Theory]
    [InlineData("MBVCB.1234.PT000123.CT tu A", "PT000123", true)]
    [InlineData("pt000123", "PT000123", true)]
    [InlineData("PT0001234", "PT000123", false)]
    [InlineData("XPT000123", "PT000123", false)]
    [InlineData("thanh toan QR-1 nhe", "QR-1", true)]
    [InlineData("QR-12", "QR-1", false)]
    [InlineData(null, "PT000123", false)]
    public void The_content_carries_a_payment_code_only_as_a_word_of_its_own(string? content, string code, bool expected) =>
        Assert.Equal(expected, BankTransferRules.ContainsPaymentCode(content, code));
}
