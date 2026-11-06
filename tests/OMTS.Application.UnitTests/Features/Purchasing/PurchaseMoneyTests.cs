using OMTS.Application.Features.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>BR-PURCHASE-02: a line is its quantity × unit price, half up to the dong (never to thousands); the total is the sum of the rounded lines.</summary>
public class PurchaseMoneyTests
{
    // Decimals, not doubles: the halves must be exact. The comments give what rounding to even would wrongly answer.
    public static TheoryData<decimal, decimal> HalfUpVectors => new()
    {
        { 0.5m, 1m }, // to even: 0
        { 1.5m, 2m },
        { 2.5m, 3m }, // to even: 2
        { 4.5m, 5m }, // to even: 4
        { 561_242.5m, 561_243m }, // to even: 561,242
        { 0m, 0m },
        { 0.49m, 0m },
        { 0.51m, 1m },
        { 2.4999m, 2m },
        { 1_000m, 1_000m },
    };

    [Theory]
    [MemberData(nameof(HalfUpVectors))]
    public void Halves_round_up_to_the_whole_dong(decimal amount, decimal expected) =>
        Assert.Equal(expected, PurchaseMoney.RoundHalfUp(amount));

    public static TheoryData<decimal, decimal, decimal> LineVectors => new()
    {
        { 12.335m, 45_500m, 561_243m }, // the PM's example: 561,242.5
        { 12.5m, 45_501m, 568_763m }, // 568,762.5, to even: 568,762
        { 1.5m, 333m, 500m }, // 499.5
        { 0.5m, 1m, 1m }, // to even: 0
        { 2.5m, 1m, 3m }, // to even: 2
        { 2.25m, 1_001m, 2_252m }, // 2,252.25
        { 3m, 499m, 1_497m }, // never to thousands
        { 2.5m, 1_300m, 3_250m },
        { 7m, 0m, 0m },
    };

    [Theory]
    [MemberData(nameof(LineVectors))]
    public void Line_amount_is_quantity_times_unit_price_half_up_to_the_dong(decimal quantity, decimal unitPrice, decimal expected) =>
        Assert.Equal(expected, PurchaseMoney.LineAmount(quantity, unitPrice));

    [Fact]
    public void The_total_is_the_sum_of_the_rounded_lines_not_the_rounded_sum()
    {
        // 0.5 + 0.5 + 0.5 = 1.5 would round to 2; the rounded lines 1 + 1 + 1 are 3.
        var receipt = Receipt((InputItemGroup.Other, 0.5m, 1), (InputItemGroup.Other, 0.5m, 1), (InputItemGroup.Other, 0.5m, 1));

        Assert.Equal(3, PurchaseMoney.Total(receipt.Lines));
        Assert.Equal(3, PurchaseMoney.Of(receipt).Total);
    }

    [Fact]
    public void A_draft_line_without_quantity_or_unit_price_is_worth_nothing_yet()
    {
        Assert.Equal(0, PurchaseMoney.LineAmount(null, 1_000m));
        Assert.Equal(0, PurchaseMoney.LineAmount(2m, null));
        Assert.Equal(2_000, PurchaseMoney.LineAmount(2m, (decimal?)1_000m));
    }

    [Fact]
    public void The_total_keeps_its_dongs_and_the_debt_share_of_a_receipt_counts_its_returns_that_are_not_cancelled()
    {
        var receipt = Receipt((InputItemGroup.Seed, 100, 9_000), (InputItemGroup.Other, 1.5m, 333));
        var line = receipt.Lines.First();
        receipt.Returns.Add(new PurchaseReturn { Lines = [new PurchaseReturnLine { ReceiptLineId = line.Id, Quantity = 10, ReturnValue = 90_000 }] });
        receipt.Returns.Add(new PurchaseReturn
        {
            StatusId = StatusIds.PurchaseReturn.Cancelled,
            Lines = [new PurchaseReturnLine { ReceiptLineId = line.Id, Quantity = 1, ReturnValue = 9_000 }],
        });

        var figures = PurchaseMoney.Of(receipt);

        Assert.Equal(900_500, figures.Total);
        Assert.Equal(900_500, PurchaseMoney.Total(receipt.Lines));
        Assert.Equal(90_000, figures.Returned);
        Assert.Equal(900_500 - 90_000, figures.DebtShare);
        Assert.Single(PurchaseMoney.RecordedReturns(receipt));
    }

    [Theory]
    [InlineData(1, "TH000001")]
    [InlineData(999_999, "TH999999")]
    [InlineData(1_000_000, "TH1000000")]
    public void Return_codes_are_TH_and_at_least_six_digits(int number, string code) =>
        Assert.Equal(code, DocumentCodes.ReturnCode(number));

    [Theory]
    [InlineData(1, "PN000001")]
    [InlineData(123, "PN000123")]
    [InlineData(999_999, "PN999999")]
    [InlineData(1_000_000, "PN1000000")]
    public void Receipt_codes_are_PN_and_at_least_six_digits(int number, string code) =>
        Assert.Equal(code, DocumentCodes.ReceiptCode(number));

    [Theory]
    [InlineData(1, "TT000001")]
    [InlineData(999_999, "TT999999")]
    [InlineData(1_000_000, "TT1000000")]
    public void Payment_codes_are_TT_and_at_least_six_digits(int number, string code) =>
        Assert.Equal(code, DocumentCodes.PaymentCode(number));

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
