using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Sales.Orders;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.UnitTests.Features.Harvest;

/// <summary>A lot is tied to a line of a harvest plan and to the product of its cycle, never to a purchase request: the cages against the line only warn, the product lines must hold the cycle product.</summary>
public class HarvestPlanLinkRulesTests
{
    private static ResolvedProductLine Line(Product product, decimal cages = 1m) => new(product, cages, cages * 20m);

    [Fact]
    public void Cages_above_the_line_only_warn_and_cages_within_it_say_nothing()
    {
        var line = new HarvestPlanLine { ExpectedCageCount = 10.5m };

        Assert.Empty(HarvestPlanCages.Warnings(line, 10.5m));
        var warning = Assert.Single(HarvestPlanCages.Warnings(line, 10.51m));
        Assert.Equal(HarvestPlanCages.ExceededCode, warning.Code);
    }

    [Fact]
    public void A_line_with_no_expected_cages_or_no_line_is_not_compared()
    {
        Assert.Empty(HarvestPlanCages.Warnings(new HarvestPlanLine(), 99m));
        Assert.Empty(HarvestPlanCages.Warnings(null, 99m));
        Assert.False(HarvestPlanCages.Reached(null, 5m));
        Assert.True(HarvestPlanCages.Reached(8m, 8m));
        Assert.False(HarvestPlanCages.Reached(8m, 7.99m));
    }

    [Fact]
    public void A_lot_holds_only_the_cycle_product_and_no_other_beside_it()
    {
        var main = new Product { Name = "Main" };
        var other = new Product { Name = "Other" };

        Assert.False(HarvestProductMatch.Matches(main.Id, [Line(main), Line(other)]));
        Assert.True(HarvestProductMatch.Matches(main.Id, [Line(main)]));
        Assert.False(HarvestProductMatch.Matches(main.Id, [Line(other)]));
    }

    [Fact]
    public void A_lot_with_no_lines_or_a_cycle_with_no_product_is_not_checked()
    {
        var other = new Product { Name = "Other" };

        Assert.True(HarvestProductMatch.Matches(Guid.NewGuid(), null));
        Assert.True(HarvestProductMatch.Matches(Guid.NewGuid(), []));
        Assert.True(HarvestProductMatch.Matches(null, [Line(other)]));
    }

    [Fact]
    public void The_refusal_names_the_reason_code_and_the_cycle_product()
    {
        var main = new Product { Name = "Hàu béo" };
        var other = new Product { Name = "Other" };
        var cycle = new CultivationCycle { ProductId = main.Id, Product = main };

        var refusal = HarvestProductMatch.Refusal(cycle, [Line(other)]);

        Assert.NotNull(refusal);
        Assert.Equal(HarvestProductMatch.ReasonCode, refusal!.Details!["reasonCode"]);
        Assert.Null(HarvestProductMatch.Refusal(cycle, [Line(main)]));
    }

    private static Product ProductWith(params (string Unit, bool IsBase, decimal Factor, decimal Price)[] units)
    {
        var product = new Product { Code = "SP000001", Name = "Hàu" };
        foreach (var (unit, isBase, factor, price) in units)
        {
            var shared = new InputUnit { Name = unit };
            product.Units.Add(new ProductUnit { ProductId = product.Id, Product = product, UnitId = shared.Id, Unit = shared, Factor = factor, Code = $"{unit}-code", IsBase = isBase, SalePrice = price });
        }

        return product;
    }

    [Fact]
    public void A_request_in_cages_takes_the_cage_unit_of_the_product_when_it_has_one()
    {
        var product = ProductWith(("kg", true, 1m, 60_000m), ("Lồng", false, 20m, 1_200_000m));

        var line = SalesOrderRequestLine.Of(product, 2.5m, kgPerCage: 25m);

        Assert.NotNull(line);
        Assert.Equal(("Lồng", 2.5m, 1_200_000m), (line!.UnitName, line.Quantity, line.UnitPrice));
    }

    [Fact]
    public void A_request_in_cages_without_a_cage_unit_is_counted_in_kg_at_the_average_kg_per_cage()
    {
        var product = ProductWith(("kg", true, 1m, 60_000m));

        var line = SalesOrderRequestLine.Of(product, 3m, kgPerCage: 25m);

        Assert.Equal(("kg", 75m, 60_000m), (line!.UnitName, line.Quantity, line.UnitPrice));
    }

    [Fact]
    public void A_product_with_no_unit_gives_no_line()
    {
        Assert.Null(SalesOrderRequestLine.Of(ProductWith(), 3m, kgPerCage: 25m));
    }

    [Fact]
    public void The_renumbered_statuses_are_all_different_from_the_ones_the_other_modules_keep()
    {
        int[] kept = [StatusIds.NurseryBatch.Nursing, StatusIds.NurseryBatch.Completed, StatusIds.NurseryBatch.Cancelled, StatusIds.NurseryBatch.Closed, StatusIds.SalesReturn.Recorded, StatusIds.SalesReturn.Cancelled, StatusIds.PurchaseRequest.AwaitingStaff];
        int[] moved =
        [
            StatusIds.ProductDisposal.Draft, StatusIds.ProductDisposal.Completed, StatusIds.ProductDisposal.Cancelled,
            StatusIds.SalesOrder.Draft, StatusIds.SalesOrder.Delivering, StatusIds.SalesOrder.Completed, StatusIds.SalesOrder.Cancelled, StatusIds.PurchaseRequest.Ordered,
        ];

        Assert.Empty(kept.Intersect(moved));
        Assert.Equal([4301, 4302, 4303, 4401, 4402, 4403, 4404, 3206], moved);
    }
}
