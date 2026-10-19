namespace OMTS.Application.Authorization;

public sealed record FunctionDefinition(string Code, string Name, bool CooperativeWide);

/// <summary>
/// Fixed function catalog roles are granted on (UC12, BR-19). The Cooperative Owner cannot add
/// functions; codes are stored in <c>RolePermission.FunctionCode</c>. Cooperative-wide functions are
/// not tied to a zone, so only grants assigned at All zones apply to them (section 4.4).
/// </summary>
public static class PermissionFunctions
{
    public const string CooperativeProfile = "COOPERATIVE_PROFILE";
    public const string Employees = "EMPLOYEES";
    public const string AccountAccess = "ACCOUNT_ACCESS";
    public const string Roles = "ROLES";
    public const string FarmingZones = "FARMING_ZONES";
    public const string Rafts = "RAFTS";
    public const string RaftMaterials = "RAFT_MATERIALS";
    public const string Catalog = "CATALOG";
    public const string SeedSuppliers = "SEED_SUPPLIERS";
    public const string SeedLots = "SEED_LOTS";
    public const string CultivationCycles = "CULTIVATION_CYCLES";
    public const string CultivationLogs = "CULTIVATION_LOGS";
    public const string Harvests = "HARVESTS";
    public const string Traceability = "TRACEABILITY";
    public const string Customers = "CUSTOMERS";
    public const string SalesOrders = "SALES_ORDERS";
    public const string CustomerPayments = "CUSTOMER_PAYMENTS";
    public const string AfterSales = "AFTER_SALES";
    public const string LaborWorkers = "LABOR_WORKERS";
    public const string WorkAssignments = "WORK_ASSIGNMENTS";
    public const string Attendance = "ATTENDANCE";
    public const string PayRules = "PAY_RULES";
    public const string Payroll = "PAYROLL";
    public const string Reports = "REPORTS";
    public const string DocumentDelivery = "DOCUMENT_DELIVERY";

    public static readonly IReadOnlyList<FunctionDefinition> All =
    [
        new(CooperativeProfile, "Cooperative profile (UC09)", CooperativeWide: true),
        new(Employees, "Employee profiles (UC10)", CooperativeWide: true),
        new(AccountAccess, "User accounts and zone-based access (UC11)", CooperativeWide: true),
        new(Roles, "Roles and permissions (UC12)", CooperativeWide: true),
        new(FarmingZones, "Farming zones and zone documents (UC15)", CooperativeWide: false),
        new(Rafts, "Rafts and lane layout (UC18)", CooperativeWide: false),
        new(RaftMaterials, "Raft materials and costs (UC76, UC77)", CooperativeWide: false),
        new(Catalog, "Oyster seed and product catalog (UC20)", CooperativeWide: true),
        new(SeedSuppliers, "Seed suppliers (UC22)", CooperativeWide: true),
        new(SeedLots, "Received seed lots (UC23)", CooperativeWide: true),
        new(CultivationCycles, "Cultivation cycles and seeding (UC24, UC25, UC27)", CooperativeWide: false),
        new(CultivationLogs, "Cultivation log and water measurements (UC26)", CooperativeWide: false),
        new(Harvests, "Harvest plans and records (UC30, UC31)", CooperativeWide: false),
        new(Traceability, "Traceability review, tracing and correction (UC32, UC36–UC39, UC41)", CooperativeWide: false),
        new(Customers, "Customer profiles (UC42)", CooperativeWide: true),
        new(SalesOrders, "Purchase requests, weighing, handover and sales (UC43–UC45, UC48)", CooperativeWide: false),
        new(CustomerPayments, "Customer payments and debt (UC49, UC50)", CooperativeWide: true),
        new(AfterSales, "Quality claims, discounts and refunds (UC52–UC54)", CooperativeWide: false),
        new(LaborWorkers, "Seasonal workers and contractors (UC55)", CooperativeWide: true),
        new(WorkAssignments, "Labor work assignment (UC56)", CooperativeWide: false),
        new(Attendance, "Attendance (UC57)", CooperativeWide: true),
        new(PayRules, "Monthly salary and daily wage rates (UC58)", CooperativeWide: true),
        new(Payroll, "Labor payment preparation, settlement and payment (UC59–UC61)", CooperativeWide: true),
        new(Reports, "Dashboard, analysis and report export (UC63–UC68)", CooperativeWide: false),
        new(DocumentDelivery, "Send documents and delivery status (UC70, UC71)", CooperativeWide: false),
    ];

    private static readonly Dictionary<string, FunctionDefinition> ByCode = All.ToDictionary(f => f.Code);

    public static bool Exists(string code) => ByCode.ContainsKey(code);

    public static bool IsCooperativeWide(string code) => ByCode.TryGetValue(code, out var function) && function.CooperativeWide;
}
