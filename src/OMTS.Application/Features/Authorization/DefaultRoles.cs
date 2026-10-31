using OMTS.Application.Features.Authorization.Models;
using static OMTS.Application.Features.Authorization.PermissionFunctions;

namespace OMTS.Application.Features.Authorization;

/// <summary>
/// Roles created at deployment for the staff actors of the 22/10 SRS (Permission Matrix §I.4.4). They are
/// created only when no role with that name exists, so permissions the Owner edited are never overwritten.
/// </summary>
public static class DefaultRoles
{
    public static readonly DefaultRole ZoneManager = new(
        "Zone Manager",
        "Runs the assigned zones: cultivation, water, harvest, purchasing, sales and workforce.",
        [
            new(CultivationCycles), new(CultivationLogs), new(WaterMeasurements), new(Harvests), new(HarvestLots),
            new(Provenance), new(PurchaseReceipts), new(Suppliers), new(Customers), new(PurchaseRequests),
            new(SalesInvoices), new(QualityClaims), new(LaborWorkers), new(WorkAssignments), new(Attendance),
            new(DashboardDailyRevenue), new(DashboardOpenHarvestLots), new(DashboardWaterAlerts), new(DashboardPendingTasks),
        ]);

    public static readonly DefaultRole Accountant = new(
        "Accountant",
        "Customer and supplier payments, debts, pay rates, payroll and sales reports; usually granted at All zones.",
        [
            new(CustomerPayments), new(CustomerDebts), new(SupplierPayments), new(PayRules), new(Payroll), new(SalesReports),
            new(SalesInvoices, [PermissionAction.View]),
            new(PurchaseReceipts, [PermissionAction.View]),
            new(HarvestLots, [PermissionAction.View]),
            // Suppliers are read to pick the payee of a supplier payment (INP-06, OMTS-209). The §I.4.4 matrix still
            // shows INP-01 as No for the Accountant; raised with the BA on OMTS-118.
            new(Suppliers, [PermissionAction.View]),
            new(DashboardDailyRevenue), new(DashboardReceivables),
        ]);

    public static readonly IReadOnlyList<DefaultRole> All = [ZoneManager, Accountant];

    /// <summary>The actions a default role holds on a function: the listed ones, or all supported ones.</summary>
    public static IReadOnlyList<PermissionAction> ActionsOf(DefaultRolePermission permission) =>
        permission.Actions ?? PermissionFunctions.Find(permission.FunctionCode)?.SupportedActions
        ?? throw new InvalidOperationException($"Unknown function '{permission.FunctionCode}'.");
}
