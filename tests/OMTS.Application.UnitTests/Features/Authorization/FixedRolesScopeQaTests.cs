using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;
using static OMTS.Application.Features.Authorization.Models.PermissionAction;
using static OMTS.Application.Features.Authorization.PermissionFunctions;

namespace OMTS.Application.UnitTests.Features.Authorization;

/// <summary>
/// QA of the fixed roles (OMTS-216 PR 0.3): the zone half of the §I.4.4 matrix. A "Full*" cell is limited to the zones
/// of the pairs, while records with no zone follow the role only (BR-FARM-08, P-6). <see cref="FixedRolesMatrixTests"/>
/// checks the actions of each cell but not this flag, so flipping a function to cooperative-wide would open every zone
/// without any test failing.
/// </summary>
public class FixedRolesScopeQaTests
{
    private static readonly Guid ZoneA = Guid.NewGuid();
    private static readonly Guid ZoneB = Guid.NewGuid();
    private static readonly Guid ZoneC = Guid.NewGuid();

    /// <summary>
    /// Function, true when its records have no zone (BR-FARM-08: suppliers, customers, payroll, pay rates, customer and
    /// supplier payments and debts). Deliberately zone-scoped for now: purchase receipts (they still carry a zone, PURCHASE
    /// ticket), purchase requests and quality claims (accepted default), employees (the cooperative-wide employee list
    /// comes with the LABOR ticket; the endpoints are Owner-only today).
    /// </summary>
    public static readonly TheoryData<string, bool> RecordsWithNoZone = new()
    {
        { Suppliers, true },
        { SupplierPayments, true },
        { Customers, true },
        { CustomerPayments, true },
        { CustomerDebts, true },
        { PayRules, true },
        { Payroll, true },
        { CultivationCycles, false },
        { CultivationLogs, false },
        { WaterMeasurements, false },
        { Harvests, false },
        { HarvestLots, false },
        { LotTracing, false },
        { Provenance, false },
        { PurchaseReceipts, false },
        { PurchaseRequests, false },
        { SalesInvoices, false },
        { QualityClaims, false },
        { SalesReturns, false },
        { LaborWorkers, false },
        { WorkAssignments, false },
        { Attendance, false },
        { SalesReports, false },
        { DashboardDailyRevenue, false },
        { DashboardReceivables, false },
        { DashboardOpenHarvestLots, false },
        { DashboardWaterAlerts, false },
        { DashboardPendingTasks, false },
    };

    [Theory]
    [MemberData(nameof(RecordsWithNoZone))]
    public void Only_functions_on_records_with_no_zone_follow_the_role_only(string function, bool noZone) =>
        Assert.Equal(noZone, IsCooperativeWide(function));

    [Fact]
    public void Every_catalog_function_has_a_zone_decision()
    {
        var decided = RecordsWithNoZone.Select(r => (string)r[0]).ToList();
        Assert.Equal(PermissionFunctions.All.Select(f => f.Code).Order(), decided.Order());
    }

    // AC-01 "Zone C – Accountant": the records with no zone open everywhere, every zone-scoped right stays in zone C.
    [Fact]
    public void One_zone_accountant_reaches_records_with_no_zone_but_zone_records_of_its_zone_only()
    {
        var access = Staff(Pair(ZoneC, FixedRoles.Accountant));

        foreach (var (function, action) in new[] { (SupplierPayments, Create), (SupplierPayments, View), (CustomerDebts, View), (CustomerPayments, Edit), (Payroll, Create), (PayRules, Edit), (Suppliers, View) })
        {
            Assert.True(access.Allows(function, action, PermissionScope.NoZone, null), $"{function} {action}");
            Assert.True(access.Allows(function, action, PermissionScope.ZoneScoped, ZoneA), $"{function} {action} on a record of zone A");
            Assert.True(access.PermittedZonesFor(function, action).AllZones, $"{function} {action}");
        }

        foreach (var function in new[] { PurchaseReceipts, SalesInvoices, LotTracing, SalesReports })
        {
            Assert.True(access.Allows(function, View, PermissionScope.ZoneScoped, ZoneC), function);
            Assert.False(access.Allows(function, View, PermissionScope.ZoneScoped, ZoneA), function);
            Assert.False(access.Allows(function, View, PermissionScope.ZoneScoped, null), function);
            var zones = access.PermittedZonesFor(function, View);
            Assert.False(zones.AllZones, function);
            Assert.Equal([ZoneC], zones.ZoneIds);
        }

        // View only on receipts and invoices (PURCHASE-03 R, SALE-09 R), nothing of the Zone Manager.
        Assert.False(access.Allows(PurchaseReceipts, Create, PermissionScope.AnyZone, null));
        Assert.False(access.Allows(PurchaseReceipts, Export, PermissionScope.AnyZone, null));
        Assert.False(access.Allows(SalesInvoices, Create, PermissionScope.ZoneScoped, ZoneC));
        Assert.False(access.Allows(Suppliers, Create, PermissionScope.AnyZone, null));
        Assert.False(access.Allows(WaterMeasurements, View, PermissionScope.ZoneScoped, ZoneC));
        Assert.True(access.PermittedZonesFor(WaterMeasurements, View).IsEmpty);
    }

    // AC-01, FARM-05 POST-2: Zone A – Zone Manager and Zone C – Accountant; each role keeps to the zones of its own pairs.
    [Fact]
    public void Two_roles_in_two_zones_never_mix_their_rights()
    {
        var access = Staff(Pair(ZoneA, FixedRoles.ZoneManager), Pair(ZoneC, FixedRoles.Accountant));

        Assert.True(access.Allows(SalesInvoices, Create, PermissionScope.ZoneScoped, ZoneA));
        Assert.False(access.Allows(SalesInvoices, Create, PermissionScope.ZoneScoped, ZoneC));
        Assert.False(access.Allows(SalesInvoices, Create, PermissionScope.ZoneScoped, ZoneB));
        Assert.True(access.Allows(SalesInvoices, View, PermissionScope.ZoneScoped, ZoneC));
        Assert.False(access.Allows(SalesInvoices, View, PermissionScope.ZoneScoped, ZoneB));
        Assert.Equal(new[] { ZoneA, ZoneC }.Order(), access.PermittedZonesFor(SalesInvoices, View).ZoneIds.Order());
        Assert.Equal([ZoneA], access.PermittedZonesFor(SalesInvoices, Export).ZoneIds);
        Assert.Equal([ZoneA], access.PermittedZonesFor(PurchaseReceipts, Delete).ZoneIds);
        Assert.False(access.Allows(WaterMeasurements, Create, PermissionScope.ZoneScoped, ZoneC));
        Assert.False(access.Allows(SalesReports, View, PermissionScope.ZoneScoped, ZoneA));

        // Records with no zone: suppliers by the Zone Manager role (Create), supplier payments by the Accountant role.
        Assert.True(access.Allows(Suppliers, Create, PermissionScope.ZoneScoped, ZoneB));
        Assert.True(access.Allows(SupplierPayments, Create, PermissionScope.NoZone, null));
        Assert.True(access.Allows(Payroll, Create, PermissionScope.ZoneScoped, ZoneA));

        var described = access.DescribeFunctions().ToDictionary(f => f.Function);
        Assert.Equal(new[] { ZoneA, ZoneC }.Order(), described[SalesInvoices].ZoneIds.Order());
        Assert.True(described[SupplierPayments].AllZones);
        Assert.Equal([ZoneA], described[WaterMeasurements].ZoneIds);
    }

    // PURCHASE-04/05, DEBT-02/03, LABOR-11/12, REPORT-01…03 are No for the Zone Manager, even at All zones; FARM-06 is Owner-only.
    [Fact]
    public void All_zones_zone_manager_gets_no_accountant_right_and_no_dashboard_tile()
    {
        var access = Staff(Pair(null, FixedRoles.ZoneManager));

        foreach (var function in new[] { SupplierPayments, CustomerPayments, CustomerDebts, PayRules, Payroll, SalesReports })
        {
            Assert.False(access.Allows(function, View, PermissionScope.NoZone, null), function);
            Assert.False(access.Allows(function, View, PermissionScope.ZoneScoped, ZoneA), function);
            Assert.True(access.PermittedZonesFor(function, View).IsEmpty, function);
        }

        Assert.All(PermissionFunctions.All.Where(f => f.IsDashboardTile), f => Assert.False(access.Allows(f.Code, View, PermissionScope.AnyZone, null), f.Code));
        Assert.True(access.Allows(WaterMeasurements, Create, PermissionScope.ZoneScoped, ZoneB));
    }

    // A pair whose role row is not a fixed role (an old custom role kept with a placeholder code) carries no permission.
    [Fact]
    public void Pair_of_a_role_outside_the_matrix_opens_nothing()
    {
        var access = Staff(new ZoneGrant(null, Guid.NewGuid(), "LEGACY-0000", "Harvest team", FixedRoles.FindByCode("LEGACY-0000")?.GrantedPermissions ?? []));

        Assert.Empty(access.DescribeFunctions());
        Assert.All(PermissionFunctions.All, f => Assert.False(access.Allows(f.Code, View, PermissionScope.NoZone, null), f.Code));
        Assert.All(PermissionFunctions.All, f => Assert.True(access.PermittedZonesFor(f.Code, View).IsEmpty, f.Code));
    }

    private static ZoneGrant Pair(Guid? zoneId, FixedRole role) => new(zoneId, Guid.NewGuid(), role.Code, role.Name, role.GrantedPermissions);

    private static AccountAccess Staff(params ZoneGrant[] grants) => new(Guid.NewGuid(), SubjectType.Staff, false, true, grants);
}
