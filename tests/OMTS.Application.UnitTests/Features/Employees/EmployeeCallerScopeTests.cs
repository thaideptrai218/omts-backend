using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.UnitTests.Features.Employees;

/// <summary>Owner decision of 24/11/2026: a caller limited to some zones sees only the employees with a working zone among them; the Owner and an all-zones caller see everyone.</summary>
public class EmployeeCallerScopeTests
{
    private static readonly Guid ZoneA = Guid.NewGuid();
    private static readonly Guid ZoneB = Guid.NewGuid();

    private static Employee In(params Guid[] zones)
    {
        var employee = new Employee { FullName = "Test" };
        foreach (var zone in zones)
        {
            employee.WorkingZones.Add(new EmployeeWorkingZone { ZoneId = zone });
        }

        return employee;
    }

    private static EmployeeCaller ManagerOf(params Guid[] zones) =>
        new(IsOwner: false, new PermittedZones(false, zones.ToHashSet()), new PermittedZones(false, zones.ToHashSet()));

    [Fact]
    public void A_manager_of_zone_A_does_not_see_an_employee_who_works_only_in_zone_B()
    {
        var manager = ManagerOf(ZoneA);

        Assert.False(manager.Sees(In(ZoneB)));
        Assert.False(manager.Sees(In()));
    }

    [Fact]
    public void A_manager_of_zone_A_sees_an_employee_who_works_in_A_and_B_through_the_zone_in_scope()
    {
        var manager = ManagerOf(ZoneA);

        Assert.True(manager.Sees(In(ZoneA)));
        Assert.True(manager.Sees(In(ZoneA, ZoneB)));
    }

    [Fact]
    public void The_owner_and_a_caller_granted_all_zones_see_everyone_and_filter_nothing()
    {
        var owner = new EmployeeCaller(IsOwner: true, PermittedZones.Everywhere);
        var everywhere = new EmployeeCaller(IsOwner: false, PermittedZones.None, PermittedZones.Everywhere);

        foreach (var caller in new[] { owner, everywhere })
        {
            Assert.True(caller.Sees(In(ZoneB)));
            Assert.True(caller.Sees(In()));
            Assert.Null(caller.VisibleZoneIds);
        }
    }

    [Fact]
    public void A_limited_caller_filters_by_its_zones_and_one_with_no_zone_sees_nobody()
    {
        Assert.Equal([ZoneA], ManagerOf(ZoneA).VisibleZoneIds);
        var none = new EmployeeCaller(IsOwner: false, PermittedZones.None);

        Assert.Empty(none.VisibleZoneIds!);
        Assert.False(none.Sees(In(ZoneA)));
    }

    [Fact]
    public void Without_view_zones_the_caller_sees_through_its_maintenance_zones()
    {
        var caller = new EmployeeCaller(IsOwner: false, new PermittedZones(false, new HashSet<Guid> { ZoneA }));

        Assert.True(caller.Sees(In(ZoneA)));
        Assert.False(caller.Sees(In(ZoneB)));
    }
}
