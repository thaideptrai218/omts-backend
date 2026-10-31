using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;
using static OMTS.Application.Features.Authorization.Models.PermissionAction;
using static OMTS.Application.Features.Authorization.PermissionFunctions;

namespace OMTS.Application.UnitTests.Features.Authorization;

/// <summary>
/// The fixed roles (BR-FARM-07) against the "Matrix by UC" of SRS 27/10/2026 §I.4.4, for the rows whose function exists
/// in the catalog. Each row names the function and the actions that make up the use case. Full / Full* = the role holds
/// every one of them; R = it holds View and none of the others; No = it holds none of them. Owner-only rows that need
/// no function (FARM-01…05, WATER-04, ACCOUNT-09/10, PURCHASE-08, LABOR-13/15/16, SALE-11, DEBT-01, HARVEST-03) are not
/// listed: no role can reach them, which the endpoint tests show.
/// </summary>
public class FixedRolesMatrixTests
{
    private const string Full = "Full";
    private const string R = "R";
    private const string No = "No";

    /// <summary>UC, Zone Manager column, Accountant column (as in the SRS), function, actions of the use case.</summary>
    public static readonly TheoryData<string, string, string, string, PermissionAction[]> Matrix = new()
    {
        { "FARM-06", No, No, DashboardDailyRevenue, [View] },
        { "FARM-06", No, No, DashboardReceivables, [View] },
        { "FARM-06", No, No, DashboardOpenHarvestLots, [View] },
        { "FARM-06", No, No, DashboardWaterAlerts, [View] },
        { "FARM-06", No, No, DashboardPendingTasks, [View] },
        // Delete = cancelling a cycle started by mistake (BR-CYCLE-01).
        { "CYCLE-01", Full, No, CultivationCycles, [Create, Delete] },
        { "CYCLE-02", Full, No, CultivationCycles, [Create, Edit] },
        { "CYCLE-03", Full, No, CultivationCycles, [Create, Edit] },
        { "CYCLE-04", Full, No, CultivationLogs, [View, Create, Edit] },
        { "CYCLE-05", Full, No, CultivationCycles, [View] },
        { "CYCLE-06", Full, No, CultivationCycles, [Edit] },
        { "WATER-01", Full, No, WaterMeasurements, [Create] },
        { "WATER-02", Full, No, WaterMeasurements, [Edit] },
        { "WATER-03", Full, No, WaterMeasurements, [View, Export] },
        { "HARVEST-01", Full, No, Harvests, [View, Create, Edit, Delete] },
        { "HARVEST-02", Full, No, HarvestLots, [View, Create, Edit] },
        { "HARVEST-04", Full, No, HarvestLots, [View, Export] },
        { "TRACE-01", Full, No, Provenance, [View, Create, Publish] },
        { "TRACE-02", Full, R, LotTracing, [View] },
        { "TRACE-03", Full, No, Provenance, [Edit] },
        // Withdrawing is an Owner-only action with no catalog action: R = View of the provenance records.
        { "TRACE-04", R, No, Provenance, [] },
        { "PURCHASE-01", Full, No, PurchaseReceipts, [Create, Edit] },
        { "PURCHASE-02", Full, No, PurchaseReceipts, [Create] },
        { "PURCHASE-03", Full, R, PurchaseReceipts, [View, Export] },
        { "PURCHASE-04", No, Full, SupplierPayments, [View, Export] },
        { "PURCHASE-05", No, Full, SupplierPayments, [Create] },
        { "PURCHASE-06", Full, No, PurchaseReceipts, [Delete] },
        { "PURCHASE-07", Full, No, Suppliers, [View, Create, Edit] },
        { "LABOR-01", Full, No, LaborWorkers, [View, Create, Edit] },
        { "LABOR-02", Full, No, WorkAssignments, [View, Create, Edit, Delete] },
        { "LABOR-04", Full, No, Attendance, [View, Create, Edit] },
        { "LABOR-11", No, Full, PayRules, [View, Create, Edit] },
        { "LABOR-12", No, Full, Payroll, [View, Create, Edit, Export] },
        { "LABOR-14", No, Full, Payroll, [Create] },
        // Responding (Edit) is Owner-only.
        { "SALE-03", R, No, PurchaseRequests, [Edit] },
        { "SALE-04", Full, No, Customers, [View, Create, Edit] },
        { "SALE-05", Full, No, SalesInvoices, [Create] },
        { "SALE-06", Full, No, SalesInvoices, [Create] },
        // Correcting (Edit) and cancelling (Delete) are Owner-only.
        { "SALE-07", R, No, SalesInvoices, [Edit] },
        { "SALE-08", R, No, SalesInvoices, [Delete] },
        { "SALE-09", Full, R, SalesInvoices, [View, Export] },
        { "DEBT-02", No, Full, CustomerPayments, [View, Create, Edit, Export] },
        { "DEBT-03", No, Full, CustomerDebts, [View, Export] },
        { "CLAIM-02", R, No, QualityClaims, [Edit] },
        { "CLAIM-03", Full, No, SalesReturns, [View, Create] },
        { "REPORT-01", No, Full, SalesReports, [View, Export] },
        { "REPORT-02", No, Full, SalesReports, [View, Export] },
        { "REPORT-03", No, Full, SalesReports, [View, Export] },
    };

    /// <summary>
    /// Where the code deliberately differs from the SRS column: the Accountant views suppliers to pick the payee of a
    /// supplier payment (PURCHASE-05). §I.4.4 shows PURCHASE-07 as No; kept by default until the BA answers (gap Q4).
    /// </summary>
    private static readonly Dictionary<(string Uc, StaffRole Role), string> Deviations = new()
    {
        [("PURCHASE-07", StaffRole.Accountant)] = R,
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Fixed_roles_follow_the_srs_matrix(string uc, string zoneManager, string accountant, string function, PermissionAction[] actions)
    {
        Assert.True(Exists(function), $"{uc}: unknown function {function}");
        Check(uc, StaffRole.ZoneManager, zoneManager, function, actions);
        Check(uc, StaffRole.Accountant, accountant, function, actions);
    }

    [Fact]
    public void A_fixed_role_holds_no_right_that_no_matrix_row_gives_it()
    {
        var rows = Matrix.Select(r => (Uc: (string)r[0], Zm: (string)r[1], Acc: (string)r[2], Function: (string)r[3], Actions: (PermissionAction[])r[4])).ToList();
        foreach (var fixedRole in FixedRoles.All)
        {
            foreach (var permission in fixedRole.Permissions)
            {
                foreach (var action in permission.Actions)
                {
                    var justified = rows.Any(row =>
                    {
                        var column = Expected(row.Uc, fixedRole.Role, fixedRole.Role == StaffRole.ZoneManager ? row.Zm : row.Acc);
                        return row.Function == permission.FunctionCode
                            && ((column == Full && row.Actions.Contains(action)) || (column == R && action == View));
                    });
                    Assert.True(justified, $"{fixedRole.Code} holds {permission.FunctionCode} {action}, which no §I.4.4 row gives it.");
                }
            }
        }
    }

    [Fact]
    public void Every_catalog_function_has_a_matrix_row()
    {
        var covered = Matrix.Select(r => (string)r[3]).ToHashSet();
        Assert.All(PermissionFunctions.All, f => Assert.Contains(f.Code, covered));
    }

    [Fact]
    public void Fixed_roles_only_hold_supported_actions_of_catalog_functions_and_always_view()
    {
        foreach (var permission in FixedRoles.All.SelectMany(r => r.Permissions))
        {
            var function = Find(permission.FunctionCode);
            Assert.NotNull(function);
            Assert.All(permission.Actions, action => Assert.True(function.Supports(action), $"{function.Code} {action}"));
            Assert.Contains(View, permission.Actions);
        }

        Assert.All(FixedRoles.All, r => Assert.Equal(r.Permissions.Count, r.Permissions.Select(p => p.FunctionCode).Distinct().Count()));
    }

    [Fact]
    public void Every_catalog_function_supports_view()
    {
        Assert.All(PermissionFunctions.All, f => Assert.True(f.Supports(View), f.Code));
        Assert.All(PermissionFunctions.All.Where(f => f.IsDashboardTile), f => Assert.Equal([View], f.SupportedActions));
    }

    [Fact]
    public void There_are_exactly_two_fixed_staff_roles_found_by_their_code()
    {
        Assert.Equal([StaffRole.ZoneManager, StaffRole.Accountant], FixedRoles.All.Select(r => r.Role));
        Assert.Equal(["ZONE_MANAGER", "ACCOUNTANT"], FixedRoles.All.Select(r => r.Code));
        Assert.Same(FixedRoles.ZoneManager, FixedRoles.FindByCode(" zone_manager "));
        Assert.Same(FixedRoles.Accountant, FixedRoles.Of(StaffRole.Accountant));
        Assert.Null(FixedRoles.FindByCode("COOPERATIVE_OWNER"));
        Assert.Null(FixedRoles.FindByCode(null));
    }

    private static void Check(string uc, StaffRole role, string srsColumn, string function, PermissionAction[] actions)
    {
        var expected = Expected(uc, role, srsColumn);
        var held = FixedRoles.Of(role).GrantedPermissions.SingleOrDefault(p => p.FunctionCode == function);
        bool Holds(PermissionAction action) => held?.Allows(action) == true;

        switch (expected)
        {
            case Full:
                Assert.True(actions.All(Holds), $"{uc}: {role} should hold {string.Join(", ", actions)} on {function} (Full).");
                break;
            case R:
                Assert.True(Holds(View), $"{uc}: {role} should view {function} (R).");
                Assert.True(actions.Where(a => a != View).All(a => !Holds(a)), $"{uc}: {role} should only view {function} (R).");
                break;
            default:
                Assert.True(actions.All(a => !Holds(a)), $"{uc}: {role} should hold none of {string.Join(", ", actions)} on {function} (No).");
                break;
        }
    }

    private static string Expected(string uc, StaffRole role, string srsColumn) =>
        Deviations.TryGetValue((uc, role), out var deviation) ? deviation : srsColumn;
}
