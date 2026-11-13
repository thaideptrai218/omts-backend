using OMTS.Application.Features.Purchasing;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>A line is typed in a unit of the item and kept in base units; its amount comes from what was typed.</summary>
public sealed class ReceiptLineUnitsTests
{
    private static PurchaseReceiptLine Line(decimal factor)
    {
        var line = new PurchaseReceiptLine { UnitFactor = factor };
        return line;
    }

    [Fact]
    public void A_line_in_the_base_unit_keeps_its_values()
    {
        var line = Line(1m);

        ReceiptLineUnits.Fill(line, 12.5m, 45_501m, 500m);

        Assert.Equal((12.5m, 45_501m, 500m), (line.Quantity, line.UnitPrice, line.Discount));
        Assert.Equal(PurchaseMoney.LineAmount(12.5m, 45_501m, 500m), line.LineAmount);
    }

    [Fact]
    public void A_line_in_a_larger_unit_is_counted_in_base_units_and_priced_by_what_was_typed()
    {
        var line = Line(12m);

        ReceiptLineUnits.Fill(line, 3m, 120_000m, 6_000m);

        Assert.Equal(36m, line.Quantity);
        Assert.Equal((3m, 120_000m, 6_000m), (line.EnteredQuantity, line.EnteredUnitPrice, line.EnteredDiscount));
        Assert.Equal(10_000m, line.UnitPrice);
        Assert.Equal(500m, line.Discount);
        // 3 boxes at 114,000 each, not 36 × a rounded price.
        Assert.Equal(342_000m, line.LineAmount);
        Assert.Equal(9_500m, ReceiptLineUnits.NetPricePerBase(line));
    }

    [Fact]
    public void The_price_per_base_unit_is_rounded_half_up_to_the_dong_but_the_cost_is_exact()
    {
        var line = Line(3m);

        ReceiptLineUnits.Fill(line, 2m, 100m, 0m);

        Assert.Equal(33m, line.UnitPrice);
        Assert.Equal(33.3333m, ReceiptLineUnits.NetPricePerBase(line));
        Assert.Equal(200m, line.LineAmount);
    }

    [Fact]
    public void A_draft_line_without_values_has_none()
    {
        var line = Line(1m);

        ReceiptLineUnits.Fill(line, null, null, 0m);

        Assert.Null(line.Quantity);
        Assert.Null(line.UnitPrice);
        Assert.Equal(0m, line.LineAmount);
    }

    [Fact]
    public void The_unit_of_the_item_is_found_by_id_or_is_the_base_unit()
    {
        var baseUnit = new InputItemUnit { IsBase = true, Factor = 1m };
        var box = new InputItemUnit { Factor = 12m };
        var item = new FarmingInputItem { Units = [baseUnit, box] };

        Assert.Same(baseUnit, ReceiptLineUnits.Resolve(item, null));
        Assert.Same(box, ReceiptLineUnits.Resolve(item, box.Id));
        Assert.Null(ReceiptLineUnits.Resolve(item, Guid.NewGuid()));
    }

    [Fact]
    public void A_return_line_in_a_unit_is_counted_in_base_units_and_valued_by_what_was_typed()
    {
        var line = new PurchaseReturnLine();

        ReceiptLineUnits.FillReturn(line, "thùng", Guid.NewGuid(), 12m, 2m, 114_000m);

        Assert.Equal((24m, 9_500m, 228_000m), (line.Quantity, line.UnitPrice, line.ReturnValue));
        Assert.Equal((2m, 114_000m, 12m, "thùng"), (line.EnteredQuantity, line.EnteredUnitPrice, line.UnitFactor, line.Unit));
    }
}
