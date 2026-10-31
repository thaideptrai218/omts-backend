using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;
using static OMTS.Application.Features.Authorization.Models.PermissionAction;
using static OMTS.Application.Features.Authorization.PermissionFunctions;

namespace OMTS.Application.Features.Authorization;

/// <summary>What a fixed role holds on one function: exactly these actions.</summary>
public sealed record FixedRolePermission(string FunctionCode, IReadOnlyList<PermissionAction> Actions);

/// <summary>A fixed staff role: its <see cref="Code"/> is the unique code of its row in the Roles table.</summary>
public sealed record FixedRole(StaffRole Role, string Code, string Name, string Description, IReadOnlyList<FixedRolePermission> Permissions)
{
    /// <summary>The permissions in the shape the permission checks use.</summary>
    public IReadOnlyList<GrantedPermission> GrantedPermissions { get; } =
        Permissions.Select(p => GrantedPermission.Of(p.FunctionCode, p.Actions)).ToList();
}

/// <summary>
/// The fixed roles and their permissions, the matrix of SRS 27/10/2026 §I.4.4 (BR-FARM-07): roles are not created,
/// edited or discontinued in OMTS, so this static matrix is the only source of what a zone–role pair allows. A pair
/// opens its role's functions in its zone (or every zone for All zones); functions on records with no zone follow the
/// role only (<see cref="FunctionDefinition.CooperativeWide"/>, BR-FARM-08). The Cooperative Owner role is not here:
/// the Owner has every right, including the Owner-only actions no role holds (FARM-06 dashboard tiles, responding to
/// purchase requests and quality claims, correcting and cancelling sales invoices, withdrawing provenance).
/// </summary>
public static class FixedRoles
{
    public const string ZoneManagerCode = "ZONE_MANAGER";
    public const string AccountantCode = "ACCOUNTANT";

    /// <summary>Field work in the assigned zones; views purchase requests, claims and invoice corrections, decides none.</summary>
    public static readonly FixedRole ZoneManager = new(
        StaffRole.ZoneManager,
        ZoneManagerCode,
        "Zone Manager",
        "Field work in the assigned zones: cultivation, water, harvest, provenance, purchasing, customers, sales invoices at the port, sales returns and workforce.",
        [
            Full(CultivationCycles), Full(CultivationLogs), Full(WaterMeasurements), Full(Harvests), Full(HarvestLots),
            Full(LotTracing), Full(Provenance), Full(PurchaseReceipts), Full(Suppliers), Full(Customers),
            new(PurchaseRequests, [View]),
            new(SalesInvoices, [View, Create, Export]),
            new(QualityClaims, [View]),
            new(SalesReturns, [View, Create]),
            Full(LaborWorkers), Full(WorkAssignments), Full(Attendance),
        ]);

    /// <summary>Office work, usually granted at All zones; views sales invoices, purchase receipts and lot traces.</summary>
    public static readonly FixedRole Accountant = new(
        StaffRole.Accountant,
        AccountantCode,
        "Accountant",
        "Customer payments, money paid back and debts, supplier payments and debts, pay rates, payroll and sales reports; usually granted at All zones.",
        [
            Full(CustomerPayments), Full(CustomerDebts), Full(SupplierPayments), Full(PayRules), Full(Payroll), Full(SalesReports),
            new(SalesInvoices, [View]),
            new(PurchaseReceipts, [View]),
            new(LotTracing, [View]),
            // Suppliers are read to pick the payee of a supplier payment (PURCHASE-05). §I.4.4 shows PURCHASE-07 as No for
            // the Accountant; kept by default until the BA answers (gap Q4, OMTS-118).
            new(Suppliers, [View]),
        ]);

    public static readonly IReadOnlyList<FixedRole> All = [ZoneManager, Accountant];

    /// <summary>
    /// The codes of the fixed roles. A role row with any other code (an Owner-made role kept by the FixedRoles migration
    /// with a placeholder code) is not a role: its pairs give no access, no login and no zone (persistence filters on this).
    /// </summary>
    public static readonly IReadOnlyList<string> Codes = [ZoneManagerCode, AccountantCode];

    public static FixedRole Of(StaffRole role) => All.Single(r => r.Role == role);

    /// <summary>The fixed role with this code (case-insensitive), or null.</summary>
    public static FixedRole? FindByCode(string? code) =>
        All.FirstOrDefault(r => string.Equals(r.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static FixedRolePermission Full(string functionCode) =>
        new(functionCode, Find(functionCode)?.SupportedActions ?? throw new InvalidOperationException($"Unknown function '{functionCode}'."));
}
