using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>SALE-05 steps 4 to 6 for an invoice of an order: the price of the order line, the share of the order discount, the advance that pays first, and the money by unit.</summary>
public class SalesInvoiceOrderDecisionsTests
{
    private readonly Product milky = new() { Name = "Hàu sữa", UnitPrice = 60_000m };
    private readonly HarvestLot lot = new() { LotCode = "KV01-B01-20261112" };
    private readonly Customer customer = new() { Name = "Chị Hoa" };

    private static ResolvedUnit Kg() => new(Guid.NewGuid(), "SP1-K", "kg", 1m, true, 60_000m);

    private SalesOrder Order(decimal discount = 0m, decimal subtotal = 6_000_000m, decimal unitPrice = 50_000m, Guid? unitId = null)
    {
        var order = new SalesOrder { Code = "DH000001", DiscountAmount = discount, Subtotal = subtotal, CustomerId = customer.Id, Customer = customer };
        order.Lines.Add(new SalesOrderLine { LineNumber = 1, ProductId = milky.Id, UnitId = unitId ?? Guid.NewGuid(), Quantity = 100m, UnitPrice = unitPrice, ListPrice = 60_000m });
        return order;
    }

    private static CustomerPayment Advance(decimal amount) =>
        new() { Kind = CustomerPaymentKind.Payment, Amount = amount, StatusId = StatusIds.CustomerPayment.Completed };

    private SalesInvoiceEntries Entries(
        decimal kg, decimal? price = null, decimal discount = 0m, decimal cash = 0m, SalesInvoiceRestPayment rest = SalesInvoiceRestPayment.None, bool priceDecided = false,
        bool discountDecided = false, bool payLaterAgreed = false) =>
        new(null, false, null, Guid.NewGuid(), null, false, [new SalesInvoiceLineEntries(1, milky.Id, lot.Id, kg, price, priceDecided)], new HashSet<Guid>(), discount, discountDecided, 0m, cash, rest, null, payLaterAgreed);

    private SalesInvoiceFacts Facts(SalesOrder order, bool byOwner = false, decimal advance = 0m, decimal shared = 0m, decimal debtBefore = 0m)
    {
        var unit = Kg();
        var resolved = new ResolvedLine(unit with { UnitId = order.Lines.First().UnitId }, 0m, order.Lines.First());
        IReadOnlyList<CustomerPayment> advances = advance > 0m ? [Advance(advance)] : [];
        return new SalesInvoiceFacts(
            customer, null, new Dictionary<Guid, Product> { [milky.Id] = milky }, [lot], Guid.NewGuid(), byOwner, debtBefore,
            new Dictionary<int, ResolvedLine> { [1] = resolved }, new SalesInvoiceOrderFacts(order, advances, new Dictionary<Guid, decimal>(), shared));
    }

    // Each line resolves its quantity from its own kg, as the resolver does.
    private static SalesInvoiceFacts With(SalesInvoiceFacts facts, decimal quantity) =>
        facts with { Resolved = new Dictionary<int, ResolvedLine> { [1] = facts.Resolved[1] with { Quantity = quantity } } };

    private (SalesInvoiceDecision Decision, string? Error) Decide(SalesInvoiceEntries entries, SalesInvoiceFacts facts)
    {
        var result = SalesInvoiceDecisions.Decide(entries, With(facts, entries.Lines[0].Kg));
        return (result.Value!, result.Error?.Message.Code);
    }

    [Fact]
    public void A_line_takes_the_price_of_the_order_line_and_says_so()
    {
        var (decision, _) = Decide(Entries(60m, cash: 3_000_000m), Facts(Order()));

        var line = Assert.Single(decision.Lines);
        Assert.Equal((50_000m, SalesPriceSource.OrderPrice, 3_000_000m, 60_000m), (line.UnitPrice, line.PriceSource, line.Amount, line.ListPrice));
        Assert.Equal(3_000_000m, decision.Total);
    }

    [Fact]
    public void A_zone_manager_changing_the_price_of_the_order_line_is_asked_and_the_owner_is_not()
    {
        var order = Order();

        var (_, error) = Decide(Entries(60m, price: 55_000m, cash: 3_300_000m), Facts(order));
        var (decided, _) = Decide(Entries(60m, price: 55_000m, cash: 3_300_000m, priceDecided: true), Facts(order));
        var (owner, _) = Decide(Entries(60m, price: 55_000m, cash: 3_300_000m), Facts(order, byOwner: true));

        Assert.Equal("MSG-SALE-10", error);
        Assert.Equal(SalesPriceSource.DecidedByOwner, decided.Lines[0].PriceSource);
        Assert.Equal(SalesPriceSource.DecidedByOwner, owner.Lines[0].PriceSource);
    }

    [Fact]
    public void A_line_in_a_cage_is_priced_by_the_cage_not_by_the_kg_weighed()
    {
        var cage = new ResolvedUnit(Guid.NewGuid(), "SP1-L", "Lồng", 20m, false, 1_300_000m);
        var order = Order(unitPrice: 1_200_000m, unitId: cage.UnitId);
        var facts = Facts(order) with { Resolved = new Dictionary<int, ResolvedLine> { [1] = new ResolvedLine(cage, 2m, order.Lines.First()) } };

        // 2 cages at 1,200,000, weighing 38.5 kg: the money follows the cages.
        var result = SalesInvoiceDecisions.Decide(Entries(38.5m, cash: 2_400_000m), facts);

        Assert.Equal(2_400_000m, result.Value!.Total);
        Assert.Equal(38.5m, result.Value.Lines[0].Entry.Kg);
    }

    [Fact]
    public void The_advance_of_the_order_pays_first_and_the_cash_pays_the_rest()
    {
        // Total 3,000,000; the advance of 500,000 pays first, so 2,500,000 of cash settles it.
        var (decision, _) = Decide(Entries(60m, cash: 2_500_000m), Facts(Order(), advance: 500_000m));

        Assert.Equal(500_000m, decision.AdvanceUse);
        Assert.Equal((2_500_000m, 0m), (decision.Cash.Recorded, decision.Cash.Rest));
        Assert.Equal(SalesPaymentTerm.PayNow, decision.Term);
    }

    [Fact]
    public void An_advance_above_the_total_pays_the_whole_invoice_with_no_cash()
    {
        var (decision, _) = Decide(Entries(60m), Facts(Order(), advance: 5_000_000m));

        Assert.Equal(3_000_000m, decision.AdvanceUse);
        Assert.Equal(0m, decision.Cash.Rest);
    }

    [Fact]
    public void With_the_advance_a_short_cash_is_refused_unless_the_rest_goes_on_pay_later()
    {
        var order = Order();

        var (_, refused) = Decide(Entries(60m, cash: 1_000_000m), Facts(order, advance: 500_000m));
        var (later, asked) = Decide(Entries(60m, cash: 1_000_000m, rest: SalesInvoiceRestPayment.PayLater, payLaterAgreed: true), Facts(order, advance: 500_000m));

        Assert.Equal("MSG-GEN-01", refused);
        Assert.Null(asked);
        Assert.Equal(1_500_000m, later.LaterAmount);
        Assert.Equal(SalesPaymentTerm.PayLater, later.Term);
    }

    [Fact]
    public void A_walk_in_buyer_takes_no_advance_and_no_pay_later()
    {
        var order = Order();
        var facts = Facts(order, advance: 500_000m) with { Customer = null };

        var (paid, _) = Decide(Entries(60m, cash: 3_000_000m), facts);
        var (_, later) = Decide(Entries(60m, rest: SalesInvoiceRestPayment.PayLater), facts);

        Assert.Equal(0m, paid.AdvanceUse);
        Assert.Equal("MSG-GEN-01", later);
    }

    [Fact]
    public void A_zone_manager_is_not_asked_about_a_discount_within_the_share_of_the_order_discount()
    {
        // The order discount 100,000 is shared in proportion: 3,000,000 of 6,000,000 takes 50,000.
        var order = Order(discount: 100_000m);

        var (within, askedWithin) = Decide(Entries(60m, discount: 50_000m, cash: 2_950_000m), Facts(order));
        var (_, askedAbove) = Decide(Entries(60m, discount: 50_001m, cash: 2_949_999m), Facts(order));
        var (_, askedNoOrderDiscount) = Decide(Entries(60m, discount: 50_000m, cash: 2_950_000m), Facts(Order()));

        Assert.Null(askedWithin);
        Assert.Equal(50_000m, within.Subtotal - within.Total);
        Assert.Equal("MSG-SALE-10", askedAbove);
        Assert.Equal("MSG-SALE-10", askedNoOrderDiscount);
    }

    [Fact]
    public void The_share_left_by_the_earlier_invoices_limits_what_needs_no_answer()
    {
        var order = Order(discount: 100_000m);

        // The earlier invoices took 80,000: only 20,000 of the order discount is left.
        var (_, asked) = Decide(Entries(60m, discount: 30_000m, cash: 2_970_000m), Facts(order, shared: 80_000m));

        Assert.Equal("MSG-SALE-10", asked);
    }
}
