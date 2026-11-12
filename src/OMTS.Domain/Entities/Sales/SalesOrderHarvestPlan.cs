using OMTS.Domain.Entities.Harvest;

namespace OMTS.Domain.Entities.Sales;

/// <summary>A harvest plan serving an order; one plan may serve several orders and an order several plans.</summary>
public class SalesOrderHarvestPlan
{
    public Guid OrderId { get; set; }
    public SalesOrder Order { get; set; } = null!;

    public Guid PlanId { get; set; }
    public HarvestPlan Plan { get; set; } = null!;
}
