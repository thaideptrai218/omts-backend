namespace OMTS.Domain.Statuses;

/// <summary>Status groups of the Status catalog (SRS 3.4): 15 life-cycle groups plus the shared USAGE group.</summary>
public static class StatusGroups
{
    public const string Usage = "USAGE";
    public const string Cooperative = "COOPERATIVE";
    public const string Account = "ACCOUNT";
    public const string Employee = "EMPLOYEE";
    public const string AccessGrant = "ACCESS_GRANT";
    public const string CultivationCycle = "CULTIVATION_CYCLE";
    public const string WaterMeasurement = "WATER_MEASUREMENT";
    public const string SalesOrder = "SALES_ORDER";
    public const string HarvestEvent = "HARVEST_EVENT";
    public const string TracePublication = "TRACE_PUBLICATION";
    public const string QualityClaim = "QUALITY_CLAIM";
    public const string PhysicalReturn = "PHYSICAL_RETURN";
    public const string SaleAdjustment = "SALE_ADJUSTMENT";
    public const string PayRule = "PAY_RULE";
    public const string LaborSettlement = "LABOR_SETTLEMENT";
    public const string MessageDelivery = "MESSAGE_DELIVERY";
}
