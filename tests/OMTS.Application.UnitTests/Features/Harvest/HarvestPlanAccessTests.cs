using OMTS.Application.Features.Harvest.Plans;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.UnitTests.Features.Harvest;

/// <summary>A plan spans the rafts of several zones: each Zone Manager sees only the lines of its zones, never a plan that was not handed over, the Owner everything.</summary>
public class HarvestPlanAccessTests
{
    private static readonly FarmingZone ZoneA = new() { Code = "A" };
    private static readonly FarmingZone ZoneB = new() { Code = "B" };

    private static HarvestPlan Plan(bool assigned, params (int Number, FarmingZone Zone)[] lines) => new()
    {
        AssignedAt = assigned ? DateTimeOffset.UtcNow : null,
        Lines = lines.Select(l => new HarvestPlanLine
        {
            LineNumber = l.Number,
            Raft = new Raft { ZoneId = l.Zone.Id, Zone = l.Zone, Code = $"R{l.Number}" },
        }).ToList(),
    };

    private static HarvestPlanAccess.Scope Manager(params FarmingZone[] zones) => new(false, zones.Select(z => z.Id).ToHashSet(), AssignedOnly: true);

    [Fact]
    public void A_zone_manager_sees_only_the_lines_of_its_zones()
    {
        var plan = Plan(assigned: true, (1, ZoneA), (2, ZoneB), (3, ZoneA));

        var seen = HarvestPlanAccess.VisibleLines(plan, Manager(ZoneA));

        Assert.Equal([1, 3], seen.Select(l => l.LineNumber));
    }

    [Fact]
    public void A_plan_with_no_line_in_the_zones_of_a_zone_manager_shows_it_nothing()
    {
        var plan = Plan(assigned: true, (1, ZoneB));

        Assert.Empty(HarvestPlanAccess.VisibleLines(plan, Manager(ZoneA)));
    }

    [Fact]
    public void A_draft_is_never_shown_to_a_zone_manager()
    {
        var plan = Plan(assigned: false, (1, ZoneA));

        Assert.Empty(HarvestPlanAccess.VisibleLines(plan, Manager(ZoneA)));
    }

    [Fact]
    public void The_owner_sees_every_line_of_every_plan_a_draft_included()
    {
        var plan = Plan(assigned: false, (1, ZoneA), (2, ZoneB));

        Assert.Equal(2, HarvestPlanAccess.VisibleLines(plan, new HarvestPlanAccess.Scope(false, null, AssignedOnly: false)).Count);
    }

    [Fact]
    public void A_caller_with_no_right_sees_nothing()
    {
        var plan = Plan(assigned: true, (1, ZoneA));

        Assert.Empty(HarvestPlanAccess.VisibleLines(plan, new HarvestPlanAccess.Scope(true, null, AssignedOnly: true)));
    }

    [Fact]
    public void The_zones_of_a_plan_are_those_of_its_rafts_by_line_number_each_once()
    {
        var plan = Plan(assigned: true, (2, ZoneB), (1, ZoneA), (3, ZoneB));

        Assert.Equal([ZoneA.Id, ZoneB.Id], HarvestPlanAccess.ZoneIdsOf(plan));
    }
}
