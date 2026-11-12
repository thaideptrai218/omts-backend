using OMTS.Application.Features.Sales.Invoices;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>What the invoices of one order share: its discount, what it delivered, its advance, and what a smaller invoice gives back of the advance.</summary>
public class SalesInvoiceOrderShareTests
{
    // The order: 100 kg at 50,000 and 2 cages at 1,200,000 is 7,400,000; the discount is 100,000.
    [Fact]
    public void The_discount_is_shared_in_proportion_to_the_goods_of_the_invoice()
    {
        // 60 kg at 50,000 is 3,000,000: 100,000 × 3,000,000 / 7,400,000 = 40,540.54, half up.
        Assert.Equal(40_541m, SalesInvoiceOrderShare.DiscountShare(100_000m, 7_400_000m, 3_000_000m, 0m));
    }

    [Theory]
    [InlineData(100_000, 1_000, 1_000, 0, 100_000)] // the only invoice takes it all
    [InlineData(100_000, 3_000, 1_000, 0, 33_333)] // a third, rounded down at .33
    [InlineData(100_000, 3_000, 2_000, 0, 66_667)] // two thirds, rounded up at .67
    [InlineData(1, 2, 1, 0, 1)] // 0.5 goes up
    public void The_share_is_rounded_half_up_to_the_dong(decimal discount, decimal orderSubtotal, decimal invoiceSubtotal, decimal sharedBefore, decimal expected) =>
        Assert.Equal(expected, SalesInvoiceOrderShare.DiscountShare(discount, orderSubtotal, invoiceSubtotal, sharedBefore));

    [Fact]
    public void The_last_invoice_takes_no_more_than_what_the_earlier_ones_left()
    {
        // Two thirds and two thirds again would be 133,334 of 100,000: the second takes what is left.
        var first = SalesInvoiceOrderShare.DiscountShare(100_000m, 3_000m, 2_000m, 0m);
        var second = SalesInvoiceOrderShare.DiscountShare(100_000m, 3_000m, 2_000m, first);

        Assert.Equal(66_667m, first);
        Assert.Equal(33_333m, second);
        Assert.Equal(0m, SalesInvoiceOrderShare.DiscountShare(100_000m, 3_000m, 2_000m, 100_000m));
    }

    [Theory]
    [InlineData(0, 1_000, 500)]
    [InlineData(100, 0, 500)]
    [InlineData(100, 1_000, 0)]
    public void Without_a_discount_or_goods_the_share_is_zero(decimal discount, decimal orderSubtotal, decimal invoiceSubtotal) =>
        Assert.Equal(0m, SalesInvoiceOrderShare.DiscountShare(discount, orderSubtotal, invoiceSubtotal, 0m));

    [Fact]
    public void Delivered_counts_what_the_other_invoices_delivered_and_the_lines_of_this_one_by_order_line()
    {
        var kg = Guid.NewGuid();
        var cage = Guid.NewGuid();

        var delivered = SalesInvoiceOrderShare.DeliveredWith(new Dictionary<Guid, decimal> { [kg] = 40m }, [(kg, 60m), (cage, 1m), (cage, 1m)]);

        Assert.Equal(100m, delivered[kg]);
        Assert.Equal(2m, delivered[cage]);
    }

    [Fact]
    public void The_order_is_delivered_when_every_line_reaches_its_quantity_in_its_own_unit()
    {
        var kg = Guid.NewGuid();
        var cage = Guid.NewGuid();
        (Guid, decimal)[] lines = [(kg, 100m), (cage, 2m)];

        Assert.False(SalesInvoiceOrderShare.AllDelivered(lines, new Dictionary<Guid, decimal> { [kg] = 100m, [cage] = 1m }));
        Assert.False(SalesInvoiceOrderShare.AllDelivered(lines, new Dictionary<Guid, decimal> { [kg] = 100m }));
        Assert.True(SalesInvoiceOrderShare.AllDelivered(lines, new Dictionary<Guid, decimal> { [kg] = 100m, [cage] = 2m }));
        // More than ordered is still delivered: the seller is not warned about going over.
        Assert.True(SalesInvoiceOrderShare.AllDelivered(lines, new Dictionary<Guid, decimal> { [kg] = 130m, [cage] = 2m }));
    }

    [Fact]
    public void The_oldest_advance_pays_first_and_each_gives_what_it_has_left()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        var uses = SalesInvoiceOrderShare.AdvancePlan([new(first, 300_000m), new(second, 500_000m), new(third, 100_000m)], need: 700_000m);

        Assert.Equal([new AdvanceUse(first, 300_000m), new AdvanceUse(second, 400_000m)], uses);
    }

    [Fact]
    public void An_advance_larger_than_the_invoice_pays_only_the_invoice_and_a_spent_one_pays_nothing()
    {
        var spent = Guid.NewGuid();
        var open = Guid.NewGuid();

        var uses = SalesInvoiceOrderShare.AdvancePlan([new(spent, 0m), new(open, 500_000m)], need: 200_000m);

        Assert.Equal([new AdvanceUse(open, 200_000m)], uses);
        Assert.Empty(SalesInvoiceOrderShare.AdvancePlan([new(open, 500_000m)], need: 0m));
    }

    [Fact]
    public void A_smaller_invoice_cuts_its_newest_parts_first_so_the_earlier_advance_is_kept()
    {
        var newest = new AdvancePart(Guid.NewGuid(), 400_000m);
        var older = new AdvancePart(Guid.NewGuid(), 300_000m);

        // The invoice can hold 500,000 of 700,000: 200,000 goes, all from the newest part.
        var cuts = SalesInvoiceOrderShare.Trim([newest, older], cap: 500_000m);

        Assert.Equal([(newest, 200_000m), (older, 300_000m)], cuts);
    }

    [Fact]
    public void A_cut_deeper_than_the_newest_part_removes_it_and_goes_on()
    {
        var newest = new AdvancePart(Guid.NewGuid(), 400_000m);
        var older = new AdvancePart(Guid.NewGuid(), 300_000m);

        var cuts = SalesInvoiceOrderShare.Trim([newest, older], cap: 100_000m);

        Assert.Equal([(newest, 0m), (older, 100_000m)], cuts);
    }

    [Fact]
    public void Nothing_is_cut_while_the_invoice_still_holds_its_parts()
    {
        var part = new AdvancePart(Guid.NewGuid(), 300_000m);

        Assert.Equal([(part, 300_000m)], SalesInvoiceOrderShare.Trim([part], cap: 300_000m));
        Assert.Equal([(part, 300_000m)], SalesInvoiceOrderShare.Trim([part], cap: 900_000m));
        Assert.Equal([(part, 0m)], SalesInvoiceOrderShare.Trim([part], cap: -50_000m));
    }

    [Fact]
    public void The_price_of_a_kg_is_the_line_price_for_kg_and_the_amount_over_the_kg_for_another_unit()
    {
        // 60 kg at 50,000 a kg.
        Assert.Equal(50_000m, InvoiceMoney.PricePerKg(60m, 60m, 50_000m, 3_000_000m));
        // 2 cages at 1,200,000 weighing 38.5 kg: 2,400,000 / 38.5 = 62,337.66.
        Assert.Equal(62_338m, InvoiceMoney.PricePerKg(38.5m, 2m, 1_200_000m, 2_400_000m));
    }
}
