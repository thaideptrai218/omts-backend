using OMTS.Application.Features.Authorization.Models;
using static OMTS.Application.Features.Authorization.Models.PermissionAction;

namespace OMTS.Application.Features.Authorization;

/// <summary>
/// Fixed catalog of the functions the fixed staff roles hold (BR-FARM-07, SRS 27/10/2026 §I.4.4): one function per
/// use-case group a Zone Manager or an Accountant uses, plus the operations dashboard tiles (FARM-06, Owner only).
/// What each role holds is <see cref="FixedRoles"/>. Owner-only use cases (cooperative profile, zones, licenses, rafts,
/// zone access, accounts, audit log, water thresholds, harvest lot parameters, products, farming input items,
/// receiving bank account, attendance and payroll settings, payroll finalization, and the Owner-only actions named in
/// the function names below) need no function: the Cooperative Owner has every right and no role opens them.
/// </summary>
public static class PermissionFunctions
{
    public const string CultivationCycles = "CULTIVATION_CYCLES";
    public const string CultivationLogs = "CULTIVATION_LOGS";
    public const string WaterMeasurements = "WATER_MEASUREMENTS";
    public const string Harvests = "HARVESTS";
    public const string HarvestLots = "HARVEST_LOTS";
    public const string LotTracing = "LOT_TRACING";
    public const string Provenance = "PROVENANCE";
    public const string Suppliers = "SUPPLIERS";
    public const string PurchaseReceipts = "PURCHASE_RECEIPTS";
    public const string SupplierPayments = "SUPPLIER_PAYMENTS";
    public const string Customers = "CUSTOMERS";
    public const string PurchaseRequests = "PURCHASE_REQUESTS";
    public const string SalesInvoices = "SALES_INVOICES";
    public const string QualityClaims = "QUALITY_CLAIMS";
    public const string SalesReturns = "SALES_RETURNS";
    public const string CustomerPayments = "CUSTOMER_PAYMENTS";
    public const string CustomerDebts = "CUSTOMER_DEBTS";
    public const string LaborWorkers = "LABOR_WORKERS";
    public const string WorkAssignments = "WORK_ASSIGNMENTS";
    public const string Attendance = "ATTENDANCE";
    public const string PayRules = "PAY_RULES";
    public const string Payroll = "PAYROLL";
    public const string SalesReports = "SALES_REPORTS";

    public const string DashboardDailyRevenue = "DASHBOARD_DAILY_REVENUE";
    public const string DashboardReceivables = "DASHBOARD_RECEIVABLES";
    public const string DashboardOpenHarvestLots = "DASHBOARD_OPEN_HARVEST_LOTS";
    public const string DashboardWaterAlerts = "DASHBOARD_WATER_ALERTS";
    public const string DashboardPendingTasks = "DASHBOARD_PENDING_TASKS";

    private static readonly PermissionAction[] Crud = [View, Create, Edit, Delete];

    // CooperativeWide = the records have no zone, so they follow the role only (BR-FARM-08): any Effective pair of a
    // role holding the function opens every record. Purchase receipts still carry a zone in this version (PURCHASE).
    public static readonly IReadOnlyList<FunctionDefinition> All =
    [
        new(CultivationCycles, "Cultivation cycles, seeding, material usage and progress (CYCLE-01, CYCLE-02, CYCLE-03, CYCLE-05, CYCLE-06)", false, Crud),
        new(CultivationLogs, "Cultivation logs (CYCLE-04)", false, [View, Create, Edit]),
        new(WaterMeasurements, "Water measurements, corrections and history (WATER-01…WATER-03)", false, [View, Create, Edit, Export]),
        new(Harvests, "Harvest records (HARVEST-01)", false, Crud),
        new(HarvestLots, "Harvest lots and farming yield (HARVEST-02, HARVEST-04)", false, [View, Create, Edit, Export]),
        new(LotTracing, "Lot tracing (TRACE-02)", false, [View]),
        new(Provenance, "Provenance publication and corrections (TRACE-01, TRACE-03; withdrawing, TRACE-04, is Owner-only)", false, [View, Create, Edit, Publish]),
        new(Suppliers, "Suppliers (PURCHASE-07)", true, [View, Create, Edit]),
        new(PurchaseReceipts, "Purchase receipts, returns and cancellations (PURCHASE-01, PURCHASE-02, PURCHASE-03, PURCHASE-06)", false, [View, Create, Edit, Delete, Export]),
        new(SupplierPayments, "Supplier payments and supplier debts (PURCHASE-04, PURCHASE-05)", true, [View, Create, Export]),
        new(Customers, "Customers (SALE-04)", true, [View, Create, Edit]),
        new(PurchaseRequests, "Purchase requests (SALE-03; responding is Owner-only)", false, [View, Edit]),
        new(SalesInvoices, "Sales invoices (SALE-05, SALE-06, SALE-09; correcting and cancelling, SALE-07 and SALE-08, are Owner-only)", false, [View, Create, Edit, Delete, Export]),
        new(QualityClaims, "Quality claims (CLAIM-02; responding is Owner-only)", false, [View, Edit]),
        new(SalesReturns, "Sales returns (CLAIM-03; cancelling one is Owner-only)", false, [View, Create, Delete]),
        new(CustomerPayments, "Customer payments and money paid back (DEBT-02)", true, [View, Create, Edit, Export]),
        new(CustomerDebts, "Customer debts (DEBT-03)", true, [View, Export]),
        new(LaborWorkers, "Employees (LABOR-01)", false, [View, Create, Edit]),
        new(WorkAssignments, "Work assignment (LABOR-02)", false, Crud),
        new(Attendance, "Attendance (LABOR-04)", false, [View, Create, Edit]),
        new(PayRules, "Job types and pay rates (LABOR-11)", true, [View, Create, Edit]),
        new(Payroll, "Payroll preparation and wage payments (LABOR-12, LABOR-14; finalizing, LABOR-13, is Owner-only)", true, [View, Create, Edit, Export]),
        new(SalesReports, "Sales, price and post-sale reports (REPORT-01…REPORT-03)", false, [View, Export]),

        new(DashboardDailyRevenue, "Dashboard tile: daily revenue (FARM-06, Owner only)", false, [View], IsDashboardTile: true),
        new(DashboardReceivables, "Dashboard tile: receivables (FARM-06, Owner only)", false, [View], IsDashboardTile: true),
        new(DashboardOpenHarvestLots, "Dashboard tile: open harvest lots (FARM-06, Owner only)", false, [View], IsDashboardTile: true),
        new(DashboardWaterAlerts, "Dashboard tile: water alerts (FARM-06, Owner only)", false, [View], IsDashboardTile: true),
        new(DashboardPendingTasks, "Dashboard tile: pending tasks (FARM-06, Owner only)", false, [View], IsDashboardTile: true),
    ];

    private static readonly Dictionary<string, FunctionDefinition> ByCode = All.ToDictionary(f => f.Code);

    public static bool Exists(string code) => ByCode.ContainsKey(code);

    public static FunctionDefinition? Find(string code) => ByCode.GetValueOrDefault(code);

    public static bool IsCooperativeWide(string code) => ByCode.TryGetValue(code, out var function) && function.CooperativeWide;
}
