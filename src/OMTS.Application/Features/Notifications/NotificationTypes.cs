using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using static OMTS.Application.Features.Notifications.NotificationPushPolicy;
using static OMTS.Application.Features.Notifications.NotificationRole;

namespace OMTS.Application.Features.Notifications;

/// <summary>
/// The notification types (BR-ACCOUNT-11): each module names the types it sends and their recipients. A module that
/// sends a new type registers it in <see cref="All"/>; the dispatcher refuses a type that is not registered.
/// </summary>
public static class NotificationTypes
{
    /// <summary>MSG-WATER-03 to the Owner and the Zone Managers of the zone (BR-WATER-05); mandatory.</summary>
    public const string WaterAlert = "WATER_ALERT";

    /// <summary>MSG-WATER-04, the withdrawal of a water alert after a correction or void (BR-WATER-05; WATER-02).</summary>
    public const string WaterAlertWithdrawn = "WATER_ALERT_WITHDRAWN";

    /// <summary>MSG-WATER-06, the reminder to measure the water of a zone (GFR-04).</summary>
    public const string WaterMeasurementOverdue = "WATER_MEASUREMENT_OVERDUE";

    /// <summary>MSG-FARM-01/02, the expiry reminders of zone licenses (GFR-03), to the Cooperative Owner only; mandatory.</summary>
    public const string ZoneLicenseExpiry = "ZONE_LICENSE_EXPIRY";

    /// <summary>
    /// MSG-FARM-01/02, the expiry reminders of cooperative certifications (GFR-03), to the Cooperative Owner only; mandatory.
    /// Its own type, beside the license one: it opens another record (FARM-01, not FARM-03) and its message parameters
    /// name a certification, so each type keeps one parameter shape.
    /// </summary>
    public const string CooperativeCertificationExpiry = "COOPERATIVE_CERTIFICATION_EXPIRY";

    /// <summary>MSG-LABOR-02, the reminder to lock a staff account after an end of employment: kept, never pushed (BR-LABOR-04).</summary>
    public const string StaffAccountLockReminder = "STAFF_ACCOUNT_LOCK_REMINDER";

    /// <summary>MSG-SALE-01, a new purchase request, to the Owner (BR-SALE-03); mandatory (BR-ACCOUNT-11).</summary>
    public const string PurchaseRequestNew = "PURCHASE_REQUEST_NEW";

    /// <summary>MSG-SALE-02, a trader cancelled a purchase request, to the Owner (BR-SALE-03).</summary>
    public const string PurchaseRequestCancelled = "PURCHASE_REQUEST_CANCELLED";

    /// <summary>MSG-SALE-03, the response to a purchase request, to the trader account (BR-SALE-03).</summary>
    public const string PurchaseRequestResponse = "PURCHASE_REQUEST_RESPONSE";

    /// <summary>MSG-PURCHASE-01, the new purchase receipt, to the Owner when a receipt is completed (BR-PURCHASE-05); push can be turned off.</summary>
    public const string PurchaseReceiptCompleted = "PURCHASE_RECEIPT_COMPLETED";

    /// <summary>MSG-PURCHASE-08, a receipt cancellation after which the supplier debt is below zero, to the Accountant and the Owner (BR-PURCHASE-05).</summary>
    public const string PurchaseReceiptCancelledPaidInAdvance = "PURCHASE_RECEIPT_CANCELLED_ADVANCE";

    /// <summary>MSG-PURCHASE-04, a supplier payment, money paid back or a cancelled payment, to the Owner (BR-PURCHASE-05); mandatory.</summary>
    public const string SupplierTransaction = "SUPPLIER_TRANSACTION";

    public static readonly IReadOnlyList<NotificationTypeDefinition> All =
    [
        new(WaterAlert, "Water alert", AlwaysPush, [CooperativeOwner, ZoneManager],
            NotificationOpenAccess.InZone(PermissionFunctions.WaterMeasurements, PermissionAction.View),
            NotificationSmsRule.MessageTextTo(CooperativeOwner, ZoneManager)),
        // The withdrawal is part of the mandatory water alert: BR-ACCOUNT-11 names WATER-02, which sends it (gap Q21).
        new(WaterAlertWithdrawn, "Water alert withdrawn", AlwaysPush, [CooperativeOwner, ZoneManager],
            NotificationOpenAccess.InZone(PermissionFunctions.WaterMeasurements, PermissionAction.View),
            NotificationSmsRule.MessageTextTo(CooperativeOwner, ZoneManager)),
        // Opens Record water measurement (WATER-01) with the zone chosen (GFR-04).
        new(WaterMeasurementOverdue, "Water measurement reminder", Default, [CooperativeOwner, ZoneManager],
            NotificationOpenAccess.InZone(PermissionFunctions.WaterMeasurements, PermissionAction.Create)),
        new(ZoneLicenseExpiry, "Zone license expiry reminder", AlwaysPush, [CooperativeOwner], NotificationOpenAccess.OwnerOnly,
            NotificationSmsRule.MessageTextTo(CooperativeOwner)),
        new(CooperativeCertificationExpiry, "Cooperative certification expiry reminder", AlwaysPush, [CooperativeOwner], NotificationOpenAccess.OwnerOnly,
            NotificationSmsRule.MessageTextTo(CooperativeOwner)),
        new(StaffAccountLockReminder, "Reminder to lock a staff account", NeverPush, [CooperativeOwner], NotificationOpenAccess.OwnerOnly),
        // Opens Respond to purchase request (SALE-03.2).
        new(PurchaseRequestNew, "New purchase request", AlwaysPush, [CooperativeOwner], NotificationOpenAccess.OwnerOnly,
            NotificationSmsRule.MessageTextTo(CooperativeOwner)),
        new(PurchaseRequestCancelled, "Purchase request cancelled", Default, [CooperativeOwner], NotificationOpenAccess.OwnerOnly),
        // Opens Track purchases (SALE-10.2), for the trader the request belongs to now.
        new(PurchaseRequestResponse, "Response to a purchase request", Default, [Trader], NotificationOpenAccess.TraderOfTheRecord),
        // Purchase receipts have no zone: opening one follows the role only (BR-FARM-08). Only the transaction of the
        // Accountant (MSG-PURCHASE-04, PURCHASE-05) is mandatory in FE-08 (BR-ACCOUNT-11).
        new(PurchaseReceiptCompleted, "New purchase receipt", Default, [CooperativeOwner],
            NotificationOpenAccess.InZone(PermissionFunctions.PurchaseReceipts, PermissionAction.View)),
        new(PurchaseReceiptCancelledPaidInAdvance, "Supplier paid in advance after a receipt cancellation", Default, [CooperativeOwner, Accountant],
            NotificationOpenAccess.InZone(PermissionFunctions.PurchaseReceipts, PermissionAction.View)),
        new(SupplierTransaction, "Supplier payment transaction", AlwaysPush, [CooperativeOwner], NotificationOpenAccess.OwnerOnly,
            NotificationSmsRule.MoneyToOwner("amount")),
    ];

    private static readonly Dictionary<string, NotificationTypeDefinition> ByCode = All.ToDictionary(t => t.Code, StringComparer.Ordinal);

    /// <summary>The registered type, or null.</summary>
    public static NotificationTypeDefinition? Find(string? code) => code is null ? null : ByCode.GetValueOrDefault(code);

    /// <summary>The types a person holding <paramref name="roles"/> can receive and that are offered in the preferences.</summary>
    public static IReadOnlyList<NotificationTypeDefinition> OfferedTo(IReadOnlySet<NotificationRole> roles) =>
        All.Where(t => t.PushPolicy != NeverPush && t.Roles.Any(roles.Contains)).ToList();
}
