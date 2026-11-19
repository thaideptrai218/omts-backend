using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Reports.EndOfDay;

namespace OMTS.Application.UnitTests.Features.Reports;

/// <summary>The figures of the end-of-day report: the sales by kind, the discount shared over invoice lines, the goods per product.</summary>
public class EndOfDayCalculatorTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 19, 9, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public void A_discount_is_shared_by_amount_to_the_whole_dong_with_the_rest_on_the_last_line()
    {
        var shares = EndOfDayCalculator.AllocateDiscount([100_000m, 100_000m, 100_000m], 10_000m);
        Assert.Equal([3_333m, 3_333m, 3_334m], shares);
        Assert.Equal(10_000m, shares.Sum());
    }

    [Fact]
    public void A_share_follows_the_weight_of_the_line()
    {
        var shares = EndOfDayCalculator.AllocateDiscount([300_000m, 100_000m], 20_000m);
        Assert.Equal([15_000m, 5_000m], shares);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void No_discount_gives_no_share(int discount) =>
        Assert.Equal([0m, 0m], EndOfDayCalculator.AllocateDiscount([50_000m, 70_000m], discount));

    [Fact]
    public void A_discount_above_the_amounts_is_cut_so_no_line_goes_below_zero()
    {
        var shares = EndOfDayCalculator.AllocateDiscount([40_000m, 60_000m], 150_000m);
        Assert.Equal([40_000m, 60_000m], shares);
    }

    [Fact]
    public void Lines_without_an_amount_get_no_share() => Assert.Equal([0m], EndOfDayCalculator.AllocateDiscount([0m], 1_000m));

    [Fact]
    public void An_order_is_not_revenue_but_its_deposit_is_money_received()
    {
        var facts = new EndOfDaySalesFacts(
            [new EndOfDayOrderFact(Guid.NewGuid(), "DH000001", At, null, null, null, 500m, 5_000_000m, 100_000m, 1_000_000m)],
            [new EndOfDayInvoiceFact(Guid.NewGuid(), "HD000001", At, null, null, "Zone A", 100m, 1_000_000m, 50_000m, 20_000m, 970_000m, 970_000m, 970_000m, 0m)],
            []);

        var sales = EndOfDayCalculator.SalesOf(facts);

        var order = sales.Groups.Single(g => g.Kind == "Order");
        Assert.Equal(1, order.Count);
        Assert.Equal(5_000_000m, order.Revenue);
        Assert.Equal(1_000_000m, order.Paid);
        Assert.Equal(0m, order.Debt);

        // Only the invoice is revenue: kg, revenue, discount and other charges leave the order out; the deposit is money received.
        Assert.Equal(100m, sales.Totals.QuantityKg);
        Assert.Equal(1_000_000m, sales.Totals.Revenue);
        Assert.Equal(50_000m, sales.Totals.Discount);
        Assert.Equal(20_000m, sales.Totals.OtherCharges);
        Assert.Equal(970_000m + 1_000_000m, sales.Totals.Paid);
        Assert.Equal(950_000m, sales.Totals.NetRevenue);
    }

    [Fact]
    public void The_three_groups_are_always_there_in_the_order_order_invoice_return()
    {
        var sales = EndOfDayCalculator.SalesOf(new EndOfDaySalesFacts([], [], []));
        Assert.Equal(["Order", "Invoice", "Return"], sales.Groups.Select(g => g.Kind).ToArray());
        Assert.All(sales.Groups, g => Assert.Equal(0, g.Count));
        Assert.Equal(0m, sales.Totals.NetRevenue);
    }

    [Fact]
    public void A_return_is_negative_and_the_debt_of_an_invoice_is_what_is_left_after_payments_and_the_return()
    {
        var invoiceId = Guid.NewGuid();
        var facts = new EndOfDaySalesFacts(
            [],
            [
                new EndOfDayInvoiceFact(invoiceId, "HD000002", At, null, null, "Zone A", 10m, 400_000m, 0m, 0m, 400_000m, 100_000m, 100_000m, 0m),
                new EndOfDayInvoiceFact(Guid.NewGuid(), "HD000003", At, null, null, "Zone A", 5m, 300_000m, 0m, 0m, 300_000m, 0m, 0m, 0m),
                new EndOfDayInvoiceFact(Guid.NewGuid(), "HD000004", At, null, null, "Zone A", 5m, 200_000m, 0m, 0m, 200_000m, 0m, 500_000m, 0m),
            ],
            [new EndOfDayReturnFact(Guid.NewGuid(), "TL000001", At, invoiceId, "HD000002", null, null, "Zone A", 10m, 400_000m)]);

        var sales = EndOfDayCalculator.SalesOf(facts);

        var invoices = sales.Groups.Single(g => g.Kind == "Invoice");
        Assert.Equal([300_000m, 300_000m, 0m], invoices.Rows.Select(r => r.Debt).ToArray());
        var returns = sales.Groups.Single(g => g.Kind == "Return");
        Assert.Equal(-400_000m, returns.Rows.Single().Revenue);
        Assert.Equal(-10m, returns.Rows.Single().QuantityKg);
        Assert.Equal(900_000m - 400_000m, sales.Totals.Revenue);
        Assert.Equal(20m - 10m, sales.Totals.QuantityKg);

        var summary = EndOfDayCalculator.SummaryOf(sales);
        Assert.Equal(400_000m, summary.ReturnValue);
        Assert.Equal(900_000m, summary.Revenue);
        Assert.Equal(500_000m, summary.NetRevenue);
    }

    [Fact]
    public void A_full_return_of_an_invoice_with_other_charges_moves_them_to_the_other_charges_column_so_the_net_revenue_is_zero()
    {
        var invoiceId = Guid.NewGuid();
        var facts = new EndOfDaySalesFacts(
            [],
            [new EndOfDayInvoiceFact(invoiceId, "HD000010", At, null, null, "Zone A", 10m, 3_000_000m, 0m, 200_000m, 3_200_000m, 0m, 0m, 3_200_000m)],
            [new EndOfDayReturnFact(Guid.NewGuid(), "TL000010", At, invoiceId, "HD000010", null, null, "Zone A", 10m, 3_200_000m, 200_000m)]);

        var sales = EndOfDayCalculator.SalesOf(facts);

        var back = sales.Groups.Single(g => g.Kind == "Return").Rows.Single();
        Assert.Equal(-3_000_000m, back.Revenue);
        Assert.Equal(-200_000m, back.OtherCharges);
        Assert.Equal(0m, sales.Totals.Revenue);
        Assert.Equal(0m, sales.Totals.OtherCharges);
        Assert.Equal(0m, sales.Totals.NetRevenue);
        Assert.Equal(3_000_000m, EndOfDayCalculator.SummaryOf(sales).ReturnValue);
    }

    [Fact]
    public void An_invoice_that_stands_returned_owes_nothing_more_than_what_the_return_leaves()
    {
        var facts = new EndOfDaySalesFacts(
            [],
            [new EndOfDayInvoiceFact(Guid.NewGuid(), "HD000005", At, null, null, "Zone A", 10m, 400_000m, 0m, 0m, 400_000m, 0m, 0m, 400_000m)],
            []);

        Assert.Equal(0m, EndOfDayCalculator.SalesOf(facts).Groups.Single(g => g.Kind == "Invoice").Rows.Single().Debt);
    }

    private static EndOfDayLineFact Line(
        Guid invoice, string code, int number, Guid product, string name, decimal kg, decimal amount, decimal discount, Guid? category = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), invoice, code, At, "Khách A", number, product, "P-" + name, name, category, kg, amount, discount);

    [Fact]
    public void Goods_revenue_is_after_the_share_of_the_invoice_discount()
    {
        var invoice = Guid.NewGuid();
        var oyster = Guid.NewGuid();
        var clam = Guid.NewGuid();
        var facts = new EndOfDayGoodsFacts(
            [
                Line(invoice, "HD1", 1, oyster, "Hàu", 10m, 300_000m, 40_000m),
                Line(invoice, "HD1", 2, clam, "Nghêu", 5m, 100_000m, 40_000m),
            ],
            [],
            []);

        var goods = EndOfDayCalculator.GoodsOf(facts, [], [], []);

        var o = goods.Products.Single(p => p.ProductId == oyster);
        var c = goods.Products.Single(p => p.ProductId == clam);
        Assert.Equal(270_000m, o.Revenue);
        Assert.Equal(90_000m, c.Revenue);
        Assert.Equal(360_000m, goods.Totals.Revenue);
        Assert.Equal("kg", o.Unit);
        Assert.Equal("Hàu", goods.Products[0].Name);
    }

    [Fact]
    public void A_product_filter_keeps_the_lines_but_does_not_change_their_value()
    {
        var invoice = Guid.NewGuid();
        var oyster = Guid.NewGuid();
        var clam = Guid.NewGuid();
        var facts = new EndOfDayGoodsFacts(
            [Line(invoice, "HD1", 1, oyster, "Hàu", 10m, 300_000m, 40_000m), Line(invoice, "HD1", 2, clam, "Nghêu", 5m, 100_000m, 40_000m)],
            [],
            []);

        var goods = EndOfDayCalculator.GoodsOf(facts, [], [clam], []);

        Assert.Equal(clam, goods.Products.Single().ProductId);
        Assert.Equal(90_000m, goods.Products.Single().Revenue);
    }

    [Fact]
    public void A_category_filter_keeps_the_products_of_the_categories_given()
    {
        var invoice = Guid.NewGuid();
        var shellfish = Guid.NewGuid();
        var other = Guid.NewGuid();
        var facts = new EndOfDayGoodsFacts(
            [
                Line(invoice, "HD1", 1, Guid.NewGuid(), "Hàu", 1m, 10_000m, 0m, shellfish),
                Line(invoice, "HD1", 2, Guid.NewGuid(), "Cá", 1m, 10_000m, 0m, other),
                Line(invoice, "HD1", 3, Guid.NewGuid(), "Tôm", 1m, 10_000m, 0m, null),
            ],
            [],
            []);

        var goods = EndOfDayCalculator.GoodsOf(facts, [], [], [shellfish]);

        Assert.Equal(["Hàu"], goods.Products.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void A_return_of_an_older_invoice_takes_its_value_from_the_share_of_that_invoice()
    {
        var oldInvoice = Guid.NewGuid();
        var oyster = Guid.NewGuid();
        var clam = Guid.NewGuid();
        var oysterLine = Guid.NewGuid();
        var returnId = Guid.NewGuid();
        var facts = new EndOfDayGoodsFacts(
            [],
            [new EndOfDayReturnedLineFact(returnId, "TL1", At, oldInvoice, "HD0", "Khách A", oysterLine, 10m)],
            [
                Line(oldInvoice, "HD0", 1, oyster, "Hàu", 10m, 300_000m, 40_000m, id: oysterLine),
                Line(oldInvoice, "HD0", 2, clam, "Nghêu", 5m, 100_000m, 40_000m),
            ]);

        var goods = EndOfDayCalculator.GoodsOf(facts, [], [], []);

        var product = goods.Products.Single();
        Assert.Equal(0m, product.SoldKg);
        Assert.Equal(10m, product.ReturnedKg);
        Assert.Equal(270_000m, product.ReturnValue);
        Assert.Equal(-270_000m, product.NetRevenue);
        var detail = product.Details.Single();
        Assert.Equal(("Return", returnId, 10m, 270_000m), (detail.Kind, detail.DocumentId, detail.Kg, detail.Value));
    }

    [Fact]
    public void Lines_of_one_product_on_one_invoice_make_one_detail()
    {
        var invoice = Guid.NewGuid();
        var oyster = Guid.NewGuid();
        var facts = new EndOfDayGoodsFacts(
            [Line(invoice, "HD1", 1, oyster, "Hàu", 10m, 100_000m, 0m), Line(invoice, "HD1", 2, oyster, "Hàu", 2m, 30_000m, 0m)],
            [],
            []);

        var product = EndOfDayCalculator.GoodsOf(facts, [], [], []).Products.Single();

        Assert.Equal(12m, product.SoldKg);
        var detail = product.Details.Single();
        Assert.Equal((12m, 130_000m), (detail.Kg, detail.Value));
    }

    [Fact]
    public void A_category_includes_everything_below_it()
    {
        var root = Guid.NewGuid();
        var child = Guid.NewGuid();
        var grandchild = Guid.NewGuid();
        var elsewhere = Guid.NewGuid();
        EndOfDayCategory[] all =
        [
            new(root, "A", null), new(child, "A1", root), new(grandchild, "A1a", child), new(elsewhere, "B", null),
        ];

        var scope = EndOfDayCalculator.WithDescendants([root], all);

        Assert.Equal(3, scope.Count);
        Assert.DoesNotContain(elsewhere, scope);
    }

    [Fact]
    public void The_money_is_added_by_fund_and_direction()
    {
        EndOfDayMoneyEntry Entry(string fund, string direction, decimal amount) =>
            new("CashVoucher", "PT1", At, direction, fund, null, null, null, amount, null, null);

        var money = EndOfDayCalculator.MoneyOf([Entry("Cash", "Receipt", 100m), Entry("Cash", "Payment", 30m), Entry("Bank", "Receipt", 50m)]);

        Assert.Equal(["Cash", "Bank"], money.ByFund.Select(f => f.Fund).ToArray());
        Assert.Equal(70m, money.ByFund[0].Net);
        Assert.Equal((150m, 30m, 120m), (money.Totals.Receipts, money.Totals.Payments, money.Totals.Net));
    }
}
