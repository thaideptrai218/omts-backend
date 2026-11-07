using OMTS.Application.Features.Sales.Invoices;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>BR-SALE-06 and BR-SALE-07: the line amounts, the total, and how the cash given at the port is split between the total, the customer debt and the change.</summary>
public class InvoiceMoneyTests
{
    public static TheoryData<decimal, decimal, decimal> LineVectors => new()
    {
        { 200m, 30_000m, 6_000_000m }, // AC-06: 200 kg of milk oysters at 30,000 VND/kg
        { 12.345m, 30_001m, 370_362m }, // 370,362.345: rounded to the dong, never to thousands
        { 0.5m, 1m, 1m }, // half up, where rounding to even gives 0
        { 2.5m, 1m, 3m }, // half up, where rounding to even gives 2
        { 0.001m, 499m, 0m }, // 0.499
        { 3m, 499m, 1_497m },
        { 150.25m, 60_000m, 9_015_000m },
    };

    [Theory]
    [MemberData(nameof(LineVectors))]
    public void A_line_is_its_kg_times_its_unit_price_rounded_half_up_to_the_dong(decimal kg, decimal unitPrice, decimal expected) =>
        Assert.Equal(expected, InvoiceMoney.LineAmount(kg, unitPrice));

    [Fact]
    public void The_total_is_the_sum_of_the_rounded_lines_minus_the_discount_plus_the_charges_minus_the_price_cuts()
    {
        // Two halves: the sum of the rounded lines (2 + 2), not the rounded sum (3).
        var subtotal = InvoiceMoney.Subtotal([InvoiceMoney.LineAmount(1.5m, 1m), InvoiceMoney.LineAmount(1.5m, 1m)]);

        Assert.Equal(4m, subtotal);
        Assert.Equal(12_000_000m - 200_000m + 50_000m - 500_000m, InvoiceMoney.Total(12_000_000m, 200_000m, 50_000m, 500_000m));
        Assert.Equal(6_000_000m, InvoiceMoney.Total(6_000_000m, 0m, 0m));
        Assert.True(InvoiceMoney.Total(100m, 101m, 0m) < 0m, "the caller refuses a discount that brings the total below 0");
    }

    public static TheoryData<decimal, decimal, decimal, bool, decimal, decimal, decimal, decimal> CashVectors => new()
    {
        // total, cash, debt before, subtract?, recorded, subtracted, change, rest
        { 6_000_000m, 6_000_000m, 0m, false, 6_000_000m, 0m, 0m, 0m }, // cash in full
        { 12_000_000m, 4_000_000m, 0m, false, 4_000_000m, 0m, 0m, 8_000_000m }, // BR-SALE-07: 4M cash, 8M later
        { 12_000_000m, 0m, 0m, false, 0m, 0m, 0m, 12_000_000m }, // all on Pay later
        { 6_000_000m, 7_000_000m, 0m, false, 6_000_000m, 0m, 1_000_000m, 0m }, // change
        { 6_000_000m, 7_000_000m, 300_000m, true, 6_300_000m, 300_000m, 700_000m, 0m }, // the extra lowers the debt only up to it
        { 6_000_000m, 7_000_000m, 5_000_000m, true, 7_000_000m, 1_000_000m, 0m, 0m }, // the whole extra lowers the debt
        { 6_000_000m, 7_000_000m, 5_000_000m, false, 6_000_000m, 0m, 1_000_000m, 0m }, // answered No: all change
        { 6_000_000m, 7_000_000m, -400_000m, true, 6_000_000m, 0m, 1_000_000m, 0m }, // money owed back: cash never adds to it
        { 0m, 0m, 0m, false, 0m, 0m, 0m, 0m }, // an invoice of 0 VND
    };

    [Theory]
    [MemberData(nameof(CashVectors))]
    public void Cash_pays_the_total_first_and_never_creates_money_owed_back(
        decimal total, decimal cash, decimal debtBefore, bool subtract, decimal recorded, decimal subtracted, decimal change, decimal rest)
    {
        var split = InvoiceMoney.SplitCash(total, cash, debtBefore, subtract);

        Assert.Equal(new PortCashSplit(recorded, subtracted, change, rest), split);
        Assert.Equal(Math.Max(0m, cash - total), split.Extra);
        Assert.True(debtBefore - split.SubtractedFromDebt >= Math.Min(0m, debtBefore), "the debt after the extra is never pushed below what it was, nor below 0");
    }

    [Fact]
    public void Msg_sale_11_is_asked_only_for_cash_above_the_total_with_a_debt_to_lower()
    {
        Assert.True(InvoiceMoney.ExtraCanLowerDebt(100m, 101m, 1m));
        Assert.False(InvoiceMoney.ExtraCanLowerDebt(100m, 100m, 1m));
        Assert.False(InvoiceMoney.ExtraCanLowerDebt(100m, 101m, 0m));
        Assert.False(InvoiceMoney.ExtraCanLowerDebt(100m, 101m, -5m));
    }

    [Fact]
    public void Kg_have_at_most_three_decimals_above_0_and_amounts_are_whole_vnd()
    {
        Assert.True(InvoiceMoney.IsKg(0.001m));
        Assert.True(InvoiceMoney.IsKg(200m));
        Assert.False(InvoiceMoney.IsKg(0m)); // AC-06: a line of 0 kg is never saved
        Assert.False(InvoiceMoney.IsKg(-1m));
        Assert.False(InvoiceMoney.IsKg(0.0001m));
        Assert.False(InvoiceMoney.IsKg(null));

        Assert.True(InvoiceMoney.IsWholeAmount(0m));
        Assert.True(InvoiceMoney.IsWholeAmount(6_000_000m));
        Assert.False(InvoiceMoney.IsWholeAmount(0.5m));
        Assert.False(InvoiceMoney.IsWholeAmount(-1m));
        Assert.False(InvoiceMoney.IsWholeAmount(null));
        Assert.False(InvoiceMoney.IsWholeAmount(1_000_000_000_000_000_000m));
    }
}
