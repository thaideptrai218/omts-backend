namespace OMTS.Domain.Enums;

/// <summary>
/// Who carries out a harvest plan, chosen by the Owner when the plan is assigned (owner decision of 25/11/2026). A plan made from the orders is harvested either by the Zone Managers of its zones
/// (Assigned, Received, Harvesting, HandedOver, then Completed by the Owner) or by the Owner himself (Assigned, Harvesting, then Completed by the Owner): no Zone Manager receives, sees or hands it over.
/// </summary>
public enum HarvestPlanExecutor
{
    ZoneManager = 0,
    Owner = 1,
}
