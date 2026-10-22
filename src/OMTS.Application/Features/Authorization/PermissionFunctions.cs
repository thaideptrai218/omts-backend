using OMTS.Application.Features.Authorization.Models;
using static OMTS.Application.Features.Authorization.Models.PermissionAction;

namespace OMTS.Application.Features.Authorization;

/// <summary>
/// Fixed catalog of grantable functions (BR-19, SRS 22/10/2026 §I.4): one function per grantable use-case
/// group plus the operations dashboard tiles (FARM-06). Codes are stored in <c>RolePermission.FunctionCode</c>.
/// Owner-only use cases (cooperative profile, zones, licenses, rafts, zone access, accounts, roles, audit
/// log, water thresholds, harvest lot parameters, products, farming input items, receiving bank account,
/// employees, payroll finalization, national portal submission) are not in the catalog: the Cooperative
/// Owner already has every right and no role can open them. Pending final confirmation in OMTS-123; keep
/// the list in this one place.
/// </summary>
public static class PermissionFunctions
{
    public const string CultivationCycles = "CULTIVATION_CYCLES";
    public const string CultivationLogs = "CULTIVATION_LOGS";
    public const string WaterMeasurements = "WATER_MEASUREMENTS";
    public const string Harvests = "HARVESTS";
    public const string HarvestLots = "HARVEST_LOTS";
    public const string Provenance = "PROVENANCE";
    public const string Suppliers = "SUPPLIERS";
    public const string PurchaseReceipts = "PURCHASE_RECEIPTS";
    public const string SupplierPayments = "SUPPLIER_PAYMENTS";
    public const string Customers = "CUSTOMERS";
    public const string PurchaseRequests = "PURCHASE_REQUESTS";
    public const string SalesInvoices = "SALES_INVOICES";
    public const string QualityClaims = "QUALITY_CLAIMS";
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

    public static readonly IReadOnlyList<FunctionDefinition> All =
    [
        new(CultivationCycles, "Cultivation cycles and seeding (CULT)", false, Crud),
        new(CultivationLogs, "Cultivation log and lane write-off (CULT)", false, [View, Create, Edit]),
        new(WaterMeasurements, "Water quality measurements (ENV-01…ENV-03)", false, [View, Create, Edit, Export]),
        new(Harvests, "Harvest plans and harvest records (HARV)", false, Crud),
        new(HarvestLots, "Harvest lots and lot tracing (TRC)", false, [View, Create, Edit, Export]),
        new(Provenance, "Provenance publication (PUB)", false, [View, Create, Edit, Publish]),
        // Cooperative-wide list, but a grant in any zone is enough to keep it (INP-01).
        new(Suppliers, "Suppliers (INP-01)", false, [View, Create, Edit]),
        new(PurchaseReceipts, "Purchase receipts and returns (INP-03…INP-05, INP-07)", false, [View, Create, Edit, Delete, Export]),
        new(SupplierPayments, "Supplier payments and supplier debt (INP-06)", true, [View, Create, Export]),
        new(Customers, "Customer profiles (SAL)", false, [View, Create, Edit]),
        new(PurchaseRequests, "Purchase request responses (SAL)", false, [View, Create, Edit]),
        new(SalesInvoices, "Sales invoices (SAL)", false, [View, Create, Edit, Delete, Export]),
        new(QualityClaims, "Quality claims and sales returns (SAL)", false, [View, Create, Edit]),
        new(CustomerPayments, "Customer payments (PAY)", true, [View, Create, Edit, Export]),
        new(CustomerDebts, "Customer debts (PAY)", true, [View, Export]),
        new(LaborWorkers, "Seasonal workers and teams (LAB-02)", false, [View, Create, Edit]),
        new(WorkAssignments, "Work assignment and piecework confirmation (LAB-03, LAB-05)", false, Crud),
        new(Attendance, "Attendance (LAB-04)", false, [View, Create, Edit]),
        new(PayRules, "Pay rates (LAB-06)", true, [View, Create, Edit]),
        new(Payroll, "Payroll preparation and wage payments (LAB-07, LAB-09)", true, [View, Create, Edit, Export]),
        new(SalesReports, "Sales and revenue reports (RPT)", false, [View, Export]),

        new(DashboardDailyRevenue, "Dashboard tile: daily revenue (FARM-06)", false, [View], IsDashboardTile: true),
        new(DashboardReceivables, "Dashboard tile: receivables (FARM-06)", false, [View], IsDashboardTile: true),
        new(DashboardOpenHarvestLots, "Dashboard tile: open harvest lots (FARM-06)", false, [View], IsDashboardTile: true),
        new(DashboardWaterAlerts, "Dashboard tile: water alerts (FARM-06)", false, [View], IsDashboardTile: true),
        new(DashboardPendingTasks, "Dashboard tile: pending tasks (FARM-06)", false, [View], IsDashboardTile: true),
    ];

    private static readonly Dictionary<string, FunctionDefinition> ByCode = All.ToDictionary(f => f.Code);

    public static bool Exists(string code) => ByCode.ContainsKey(code);

    public static FunctionDefinition? Find(string code) => ByCode.GetValueOrDefault(code);

    public static bool IsCooperativeWide(string code) => ByCode.TryGetValue(code, out var function) && function.CooperativeWide;
}
