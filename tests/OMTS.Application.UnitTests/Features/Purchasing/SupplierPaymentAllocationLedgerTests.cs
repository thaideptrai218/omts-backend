using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>
/// The parts of payments set against receipts cover those receipts first, the rest covers the oldest receipts, and the debt
/// stays the same running sum; a return defaults to what one unit was paid, the receipt discount prorated.
/// </summary>
public class SupplierPaymentAllocationLedgerTests
{
    private static readonly DateOnly Today = new(2026, 11, 10);

    private static DebtReceiptRow Receipt(string code, int daysAgo, decimal total, decimal returned = 0, decimal discount = 0) =>
        new(Guid.Empty, Guid.NewGuid(), code, Today.AddDays(-daysAgo), total, returned, discount);

    private static DebtPaymentRow Paid(string code, int daysAgo, decimal amount) =>
        new(Guid.Empty, Guid.NewGuid(), code, Today.AddDays(-daysAgo), amount, 0, PaymentMethod.Cash, Guid.Empty, 0);

    private static DebtAllocationRow Part(DebtPaymentRow payment, DebtReceiptRow receipt, decimal amount) =>
        new(Guid.Empty, payment.Id, receipt.Id, amount);

    [Fact]
    public void A_part_set_against_a_newer_receipt_pays_it_and_leaves_the_older_one_unpaid()
    {
        var (older, newer) = (Receipt("PN000001", 9, 100_000), Receipt("PN000002", 2, 50_000));
        var payment = Paid("TT000001", 1, 50_000);

        var unpaid = Assert.Single(SupplierDebtLedger.UnpaidReceipts([older, newer], 50_000, Today, [Part(payment, newer, 50_000)]));
        var allocated = SupplierDebtLedger.AllocatePayments([older, newer], [payment], 0, [Part(payment, newer, 50_000)]);

        Assert.Equal((older.Id, 100_000m), (unpaid.Id, unpaid.Remaining));
        var only = Assert.Single(allocated);
        Assert.Equal((newer.Id, payment.Id, 50_000m), (only.ReceiptId, only.PaymentId, only.Amount));
    }

    [Fact]
    public void Without_parts_the_same_payment_covers_the_oldest_receipt_and_the_debt_is_the_same()
    {
        var (older, newer) = (Receipt("PN000001", 9, 100_000), Receipt("PN000002", 2, 50_000));

        var unpaid = SupplierDebtLedger.UnpaidReceipts([older, newer], 50_000, Today);

        Assert.Equal(100_000, unpaid.Sum(u => u.Remaining));
        Assert.Equal([50_000m, 50_000m], unpaid.Select(u => u.Remaining));
    }

    [Fact]
    public void A_part_above_what_the_receipt_now_owes_covers_the_oldest_receipts_with_the_rest()
    {
        var (older, newer) = (Receipt("PN000001", 9, 100_000), Receipt("PN000002", 2, 50_000, returned: 30_000));
        var payment = Paid("TT000001", 1, 50_000);
        var part = Part(payment, newer, 50_000);

        var unpaid = Assert.Single(SupplierDebtLedger.UnpaidReceipts([older, newer], 50_000, Today, [part]));
        var allocated = SupplierDebtLedger.AllocatePayments([older, newer], [payment], 0, [part]);

        Assert.Equal((older.Id, 70_000m), (unpaid.Id, unpaid.Remaining));
        Assert.Equal(20_000, allocated.Single(a => a.ReceiptId == newer.Id).Amount);
        Assert.Equal(30_000, allocated.Single(a => a.ReceiptId == older.Id).Amount);
    }

    // The user's PN000027 case: payable 551,700, paid 550,000 against it, 200 units returned and the money paid back.
    [Fact]
    public void Money_paid_back_comes_out_of_the_part_and_what_stays_unpaid_is_the_debt()
    {
        var returned = 200 * 2_704m;
        var receipt = Receipt("PN000027", 1, 612_000, returned: returned, discount: 60_300);
        var payment = Paid("TT000010", 1, 550_000);
        var sums = new SupplierDebtSums(Receipts: 551_700, Returned: returned, Paid: 550_000, PaidBack: returned);
        var part = Part(payment, receipt, 550_000);

        var unpaid = Assert.Single(SupplierDebtLedger.UnpaidReceipts([receipt], sums.Credit, Today, [part]));
        var allocated = Assert.Single(SupplierDebtLedger.AllocatePayments([receipt], [payment], returned, [part]));

        Assert.Equal(1_700, sums.Debt);
        Assert.Equal(sums.Debt, unpaid.Remaining);
        Assert.Equal(receipt.Net - sums.Debt, allocated.Amount);
    }

    [Fact]
    public void A_return_defaults_to_the_unit_price_less_its_share_of_the_receipt_discount()
    {
        var line = new PurchaseReceiptLine { Quantity = 204, UnitPrice = 3_000, LineAmount = 612_000 };
        var receipt = new PurchaseReceipt { Discount = 60_300, Lines = [line] };

        var price = PurchaseMoney.ReturnUnitPrice(receipt, line);

        Assert.Equal(2_704, price);
        Assert.Equal(540_800, PurchaseMoney.LineAmount(200, price));
    }

    [Fact]
    public void The_receipt_discount_is_shared_by_line_amount_after_the_line_discounts()
    {
        var cheap = new PurchaseReceiptLine { Quantity = 10, UnitPrice = 1_000, Discount = 100, LineAmount = 9_000 };
        var dear = new PurchaseReceiptLine { Quantity = 1, UnitPrice = 11_000, LineAmount = 11_000 };
        var receipt = new PurchaseReceipt { Discount = 2_000, Lines = [cheap, dear] };

        Assert.Equal(810, PurchaseMoney.ReturnUnitPrice(receipt, cheap));
        Assert.Equal(9_900, PurchaseMoney.ReturnUnitPrice(receipt, dear));
        Assert.Equal(900, PurchaseMoney.ReturnUnitPrice(new PurchaseReceipt { Lines = [cheap] }, cheap));
    }
}
