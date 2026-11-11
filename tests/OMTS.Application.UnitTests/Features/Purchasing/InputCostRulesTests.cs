using OMTS.Application.Features.Purchasing.Items;
using OMTS.Domain.Enums;
using static OMTS.Domain.Enums.InputCostMethod;
using static OMTS.Domain.Enums.InputStockMovementKind;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>The cost of an input item after a movement: Fixed stays what the user set (the first receipt sets a missing one), Average is a moving average.</summary>
public sealed class InputCostRulesTests
{
    [Fact]
    public void Fixed_cost_is_set_by_the_first_receipt_only_when_there_is_none()
    {
        Assert.Equal(11_000m, InputCostRules.Adjusted(Fixed, Receipt, null, 0m, 10m, 11_000m));
        Assert.Equal(9_000m, InputCostRules.Adjusted(Fixed, Receipt, 9_000m, 5m, 10m, 11_000m));
        Assert.Null(InputCostRules.Adjusted(Fixed, PurchaseReturn, null, 5m, -2m, 11_000m));
        Assert.Null(InputCostRules.Adjusted(Fixed, Reversal, null, 5m, -5m, 11_000m));
    }

    [Fact]
    public void Average_cost_is_the_moving_average_of_the_receipts()
    {
        // 10 at 100, then 10 at 200: (10 × 100 + 10 × 200) / 20.
        Assert.Equal(150m, InputCostRules.Adjusted(Average, Receipt, 100m, 10m, 10m, 200m));
        // 5 at 100 with 15 at 200: 3500 / 20.
        Assert.Equal(175m, InputCostRules.Adjusted(Average, Receipt, 100m, 5m, 15m, 200m));
    }

    [Fact]
    public void Average_cost_starts_from_the_price_when_there_is_no_cost_yet()
    {
        Assert.Equal(200m, InputCostRules.Adjusted(Average, Receipt, null, 0m, 10m, 200m));
    }

    [Fact]
    public void A_negative_quantity_on_hand_counts_as_zero_in_the_average()
    {
        // The 3 missing units are not weighed: the new cost is the price of the 10 received.
        Assert.Equal(200m, InputCostRules.Adjusted(Average, Receipt, 100m, -3m, 10m, 200m));
    }

    [Fact]
    public void A_return_takes_the_returned_value_out_of_the_average()
    {
        // 20 at an average of 150, 10 returned at 200: (20 × 150 − 10 × 200) / 10.
        Assert.Equal(100m, InputCostRules.Adjusted(Average, PurchaseReturn, 150m, 20m, -10m, 200m));
    }

    [Fact]
    public void The_cost_stays_when_nothing_is_left_and_never_drops_below_zero()
    {
        Assert.Equal(150m, InputCostRules.Adjusted(Average, PurchaseReturn, 150m, 10m, -10m, 200m));
        Assert.Equal(0m, InputCostRules.Adjusted(Average, PurchaseReturn, 100m, 20m, -10m, 300m));
    }

    [Fact]
    public void The_cancellation_of_a_receipt_restores_the_average_it_changed()
    {
        var afterReceipt = InputCostRules.Adjusted(Average, Receipt, 100m, 10m, 10m, 200m);
        var afterCancel = InputCostRules.Adjusted(Average, Reversal, afterReceipt, 20m, -10m, 200m);

        Assert.Equal(100m, afterCancel);
    }

    [Theory]
    [InlineData(Opening)]
    [InlineData(Issue)]
    [InlineData(Disposal)]
    public void Other_movements_leave_the_average_alone(InputStockMovementKind kind) =>
        Assert.Equal(120m, InputCostRules.Adjusted(Average, kind, 120m, 10m, 5m, 300m));

    [Fact]
    public void A_row_with_no_price_leaves_the_cost_alone()
    {
        Assert.Equal(120m, InputCostRules.Adjusted(Average, Receipt, 120m, 10m, 5m, null));
        Assert.Null(InputCostRules.Adjusted(Fixed, Receipt, null, 10m, 5m, null));
    }

    [Fact]
    public void The_average_has_at_most_four_decimals()
    {
        Assert.Equal(133.3333m, InputCostRules.Adjusted(Average, Receipt, 100m, 20m, 10m, 200m));
    }
}
