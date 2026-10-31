using OMTS.Application.Features.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>BR-PURCHASE-02: a line is its quantity × unit price, to the dong only (never to thousands); the total is their sum.</summary>
public class PurchaseMoneyTests
{
    [Theory]
    [InlineData(1.5, 333, 500)]
    [InlineData(3, 499, 1497)]
    [InlineData(2.5, 1300, 3250)]
    [InlineData(12.345, 85000, 1049325)]
    [InlineData(0.001, 499, 0)]
    [InlineData(0.001, 500, 1)]
    [InlineData(2.25, 1001, 2252)]
    [InlineData(7, 0, 0)]
    public void Line_amount_is_quantity_times_unit_price_rounded_to_the_dong_away_from_zero(decimal quantity, decimal unitPrice, decimal expected) =>
        Assert.Equal(expected, PurchaseMoney.LineAmount(quantity, unitPrice));

    [Fact]
    public void A_draft_line_without_quantity_or_unit_price_is_worth_nothing_yet()
    {
        Assert.Equal(0, PurchaseMoney.LineAmount(null, 1_000m));
        Assert.Equal(0, PurchaseMoney.LineAmount(2m, null));
        Assert.Equal(2_000, PurchaseMoney.LineAmount(2m, (decimal?)1_000m));
    }

    [Fact]
    public void The_total_is_the_sum_of_the_unrounded_line_amounts_and_the_outstanding_follows_returns_payments_and_refunds()
    {
        var receipt = Receipt((InputItemGroup.Seed, 100, 9_000), (InputItemGroup.Other, 1.5m, 333));
        receipt.Payments.Add(new SupplierPayment { Amount = 300_000, StatusId = StatusIds.SupplierPayment.Recorded });
        receipt.Payments.Add(new SupplierPayment { Amount = 50_000, StatusId = StatusIds.SupplierPayment.Cancelled });
        var line = receipt.Lines.First();
        receipt.Returns.Add(new PurchaseReturn
        {
            RefundAmount = 20_000,
            DebtReductionAmount = 70_000,
            Lines = [new PurchaseReturnLine { ReceiptLineId = line.Id, Quantity = 10, ReturnValue = 90_000 }],
        });

        var figures = PurchaseMoney.Of(receipt);

        Assert.Equal(900_500, figures.Total);
        Assert.Equal(900_500, PurchaseMoney.Total(receipt.Lines));
        Assert.Equal(90_000, figures.Returned);
        Assert.Equal(280_000, figures.NetPaid);
        Assert.Equal(900_500 - 90_000 - 280_000, figures.Outstanding);
    }

    [Theory]
    [InlineData(10, 10_000, 1_000_000, 0, 100_000)]
    [InlineData(1, 500, 1_500, 0, 1_000)]
    [InlineData(1, 500, 1_500, 1_000, 1_000)]
    [InlineData(1, 500, 1_500, 2_000, 0)]
    public void A_return_value_is_whole_thousands_capped_by_what_is_left_of_its_line(
        decimal quantity, decimal unitPrice, decimal lineAmount, decimal alreadyReturned, decimal expected)
    {
        var line = new PurchaseReceiptLine { Quantity = 100, UnitPrice = unitPrice, LineAmount = lineAmount };
        Assert.Equal(expected, PurchaseMoney.CappedReturnValue(quantity, line, alreadyReturned));
    }

    [Fact]
    public void The_fit_caps_the_total_and_puts_the_remainder_on_the_last_lines()
    {
        Assert.Equal([1_000_000m, 1_000_000m, 999_000m], PurchaseMoney.FitToReceipt([1_000_000, 1_000_000, 1_000_000], 2_999_000, true));
        Assert.Equal([1_000_000m, 999_000m], PurchaseMoney.FitToReceipt([1_000_000, 1_000_000], 1_999_000, false));
        Assert.Equal([500_000m, 0m], PurchaseMoney.FitToReceipt([500_000, 500_000], 500_000, false));
        Assert.Equal([2_000_000m], PurchaseMoney.FitToReceipt([1_999_000], 2_000_000, true));
        Assert.Equal([1_000_000m], PurchaseMoney.FitToReceipt([1_000_000], 5_000_000, false));
    }

    [Theory]
    [InlineData(1, "PN000001")]
    [InlineData(123, "PN000123")]
    [InlineData(999_999, "PN999999")]
    [InlineData(1_000_000, "PN1000000")]
    public void Receipt_codes_are_PN_and_at_least_six_digits(int number, string code)
    {
        Assert.Equal(code, DocumentCodes.ReceiptCode(number));
        Assert.Equal(number, DocumentCodes.ReceiptNumber(code));
    }

    [Theory]
    [InlineData("PR-20261022-001")]
    [InlineData("PN")]
    [InlineData("PN12A")]
    [InlineData("PRT-20261022-001")]
    [InlineData(null)]
    public void Other_codes_are_not_numbers_of_the_sequence(string? code) => Assert.Null(DocumentCodes.ReceiptNumber(code));

    [Fact]
    public void The_seed_lot_code_is_the_receipt_code_and_the_line_number() =>
        Assert.Equal("PN000123-1", DocumentCodes.SeedLotCode("PN000123", 1));

    private static PurchaseReceipt Receipt(params (InputItemGroup Group, decimal Quantity, decimal UnitPrice)[] lines)
    {
        var receipt = new PurchaseReceipt { StatusId = StatusIds.PurchaseReceipt.Completed };
        var number = 0;
        foreach (var (group, quantity, unitPrice) in lines)
        {
            receipt.Lines.Add(new PurchaseReceiptLine
            {
                LineNumber = ++number,
                Item = new FarmingInputItem { Group = group },
                Quantity = quantity,
                UnitPrice = unitPrice,
                LineAmount = PurchaseMoney.LineAmount(quantity, unitPrice),
            });
        }

        return receipt;
    }
}
