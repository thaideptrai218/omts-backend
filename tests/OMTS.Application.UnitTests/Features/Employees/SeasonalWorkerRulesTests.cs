using OMTS.Application.Features.AccessManagement;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Employees;
using OMTS.Application.Common.Validation;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Employees;

/// <summary>Owner decision of 25/11/2026: a seasonal worker has no fixed zone and a login with the fixed initial password; a Staff employee keeps fixed zones and a random one-time password.</summary>
public class SeasonalWorkerRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 25);
    private static readonly Guid ZoneA = Guid.NewGuid();
    private static readonly Guid ZoneB = Guid.NewGuid();

    private static SaveEmployeeRequest Request(EmployeeType type, params Guid[] zones) =>
        new("Trần Văn A", "0912345678", null, null, null, null, null, null, zones, null, type);

    private static Employee WithType(EmployeeType type, params Guid[] zones)
    {
        var employee = new Employee { FullName = "Test" };
        employee.TypePeriods.Add(new EmployeeTypePeriod { Type = type, EffectiveFrom = new DateOnly(2026, 1, 1) });
        foreach (var zone in zones)
        {
            employee.WorkingZones.Add(new EmployeeWorkingZone { ZoneId = zone });
        }

        return employee;
    }

    private static EmployeeCaller ManagerOf(params Guid[] zones) =>
        new(IsOwner: false, new PermittedZones(false, zones.ToHashSet()), new PermittedZones(false, zones.ToHashSet()), Today);

    [Fact]
    public void A_seasonal_worker_is_added_without_a_working_zone_and_a_staff_employee_is_not()
    {
        Assert.Empty(EmployeeInput.From(Request(EmployeeType.SeasonalWorker), Today, adding: true).Invalid);
        Assert.Equal(["Working zones"], EmployeeInput.From(Request(EmployeeType.Staff), Today, adding: true).Invalid);
        Assert.Empty(EmployeeInput.From(Request(EmployeeType.Staff, ZoneA), Today, adding: true).Invalid);
    }

    [Fact]
    public void A_manager_sees_and_maintains_every_seasonal_worker_whatever_their_zones_and_a_staff_employee_only_in_their_zones()
    {
        var manager = ManagerOf(ZoneA);

        Assert.True(manager.Sees(WithType(EmployeeType.SeasonalWorker)));
        Assert.True(manager.Sees(WithType(EmployeeType.SeasonalWorker, ZoneB)));
        Assert.True(manager.Maintains(WithType(EmployeeType.SeasonalWorker)));
        Assert.False(manager.Sees(WithType(EmployeeType.Staff, ZoneB)));
        Assert.False(manager.Maintains(WithType(EmployeeType.Staff, ZoneB)));
        Assert.True(manager.Sees(WithType(EmployeeType.Staff, ZoneA)));
        Assert.True(manager.SeesSeasonalWorkers);
    }

    [Fact]
    public void A_caller_with_no_zone_sees_no_seasonal_worker()
    {
        var none = new EmployeeCaller(IsOwner: false, PermittedZones.None, null, Today);

        Assert.False(none.Sees(WithType(EmployeeType.SeasonalWorker)));
        Assert.False(none.Maintains(WithType(EmployeeType.SeasonalWorker)));
        Assert.False(none.SeesSeasonalWorkers);
    }

    [Fact]
    public void A_view_only_caller_sees_seasonal_workers_but_does_not_maintain_them()
    {
        var viewer = new EmployeeCaller(IsOwner: false, PermittedZones.None, new PermittedZones(false, new HashSet<Guid> { ZoneA }), Today);

        Assert.True(viewer.Sees(WithType(EmployeeType.SeasonalWorker)));
        Assert.False(viewer.Maintains(WithType(EmployeeType.SeasonalWorker)));
    }

    [Fact]
    public void The_employee_role_is_fixed_holds_no_function_and_is_found_by_its_code()
    {
        var role = FixedRoles.FindByCode("employee");

        Assert.Same(FixedRoles.Employee, role);
        Assert.Equal(StaffRole.Employee, role!.Role);
        Assert.Empty(role.Permissions);
        Assert.Contains(FixedRoles.EmployeeCode, FixedRoles.Codes);
        Assert.Same(FixedRoles.Employee, FixedRoles.Of(StaffRole.Employee));
    }
}
