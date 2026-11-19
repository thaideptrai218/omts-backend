using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;

namespace OMTS.Application.UnitTests.Features.Harvest;

/// <summary>The expected harvest date of a cycle is its start plus the days its seed needs to mature; a plan without a sales order is refused with MSG-PLAN-07.</summary>
public class HarvestPlanMaturityTests
{
    [Fact]
    public void The_expected_harvest_date_is_the_start_date_plus_the_maturity_days()
    {
        var cycle = new CycleMaturity(new DateOnly(2026, 3, 1), 240);

        Assert.Equal(new DateOnly(2026, 10, 27), cycle.ExpectedHarvestDate);
    }

    [Fact]
    public void Without_maturity_days_there_is_no_expected_harvest_date()
    {
        Assert.Null(new CycleMaturity(new DateOnly(2026, 3, 1), null).ExpectedHarvestDate);
    }

    [Fact]
    public void A_plan_without_a_sales_order_answers_MSG_PLAN_07_in_Vietnamese()
    {
        var message = UserMessages.PlanNeedsSalesOrder();

        Assert.Equal(MessageCodes.Plan07, message.Code);
        Assert.Contains("đơn đặt hàng", message.Text);
    }
}
