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

    /// <summary>To the Cooperative Owner: a trader asked for an account from the Zalo Mini App.</summary>
    public const string TraderAccessRequested = "TRADER_ACCESS_REQUESTED";

    /// <summary>To the trader: the Cooperative Owner approved the request.</summary>
    public const string TraderAccessApproved = "TRADER_ACCESS_APPROVED";

    /// <summary>MSG-LABOR-02, the reminder to lock a staff account after an end of employment: kept, never pushed (BR-LABOR-04).</summary>
    public const string StaffAccountLockReminder = "STAFF_ACCOUNT_LOCK_REMINDER";

    /// <summary>MSG-SALE-01, a new purchase request, to the Owner (BR-SALE-03); mandatory (BR-ACCOUNT-11).</summary>
    public const string PurchaseRequestNew = "PURCHASE_REQUEST_NEW";

    /// <summary>MSG-SALE-02, a trader cancelled a purchase request, to the Owner (BR-SALE-03).</summary>
    public const string PurchaseRequestCancelled = "PURCHASE_REQUEST_CANCELLED";

    /// <summary>MSG-SALE-03, the response to a purchase request, to the trader account (BR-SALE-03).</summary>
    public const string PurchaseRequestResponse = "PURCHASE_REQUEST_RESPONSE";

    /// <summary>MSG-CYCLE-02, the seeding of a raft is finished, to the Cooperative Owner to review it (CYCLE-02.1); once per cycle.</summary>
    public const string CycleSeedingCompleted = "CYCLE_SEEDING_COMPLETED";

    /// <summary>MSG-HARVEST-01, a recorded harvest, to the Cooperative Owner to review (BF-03 step 7); not on a correction. BR-ACCOUNT-11 does not list it as mandatory.</summary>
    public const string HarvestLotRecorded = "HARVEST_LOT_RECORDED";

    /// <summary>MSG-HARVEST-05, a lot for sale the next daily close check closes, to the Zone Managers of the zone or else the Owner (GFR-05); once per close date. BR-ACCOUNT-11 does not list it as mandatory.</summary>
    public const string HarvestLotAutoCloseWarning = "HARVEST_LOT_AUTO_CLOSE_WARNING";

    /// <summary>MSG-CYCLE-08, every seeded lane is Harvested, to the Zone Managers of the zone or else the Owner (BR-CYCLE-07); mandatory.</summary>
    public const string CycleCloseReminder = "CYCLE_CLOSE_REMINDER";

    /// <summary>MSG-CYCLE-06, the summary of a closed cycle with the figures of the close day, to the Cooperative Owner (CYCLE-06 step 6). BR-ACCOUNT-11 does not list it as mandatory.</summary>
    public const string CycleClosed = "CYCLE_CLOSED";

    /// <summary>MSG-PURCHASE-01, the new purchase receipt, to the Owner when a receipt is completed (BR-PURCHASE-05); push can be turned off.</summary>
    public const string PurchaseReceiptCompleted = "PURCHASE_RECEIPT_COMPLETED";

    /// <summary>MSG-PURCHASE-08, a receipt cancellation after which the supplier debt is below zero, to the Accountant and the Owner (BR-PURCHASE-05).</summary>
    public const string PurchaseReceiptCancelledPaidInAdvance = "PURCHASE_RECEIPT_CANCELLED_ADVANCE";

    /// <summary>MSG-PURCHASE-04, a supplier payment, money paid back or a cancelled payment, to the Owner (BR-PURCHASE-05); mandatory.</summary>
    public const string SupplierTransaction = "SUPPLIER_TRANSACTION";
    /// <summary>MSG-CYCLE-04, a new cultivation log to review, to the Cooperative Owner (CYCLE-04); not on a correction.</summary>
    public const string CultivationLogRecorded = "CULTIVATION_LOG_RECORDED";

    /// <summary>MSG-SALE-08, a new sales invoice, to the Cooperative Owner, also for the Owner's own sale (BR-SALE-03). BR-ACCOUNT-11 does not list it as mandatory.</summary>
    public const string SaleCompleted = "SALE_COMPLETED";

    /// <summary>
    /// MSG-DEBT-03, a customer payment, money paid back or a cancellation saved in DEBT-02, or one list of confirmations, to the Owner, also for the Owner's own action
    /// (BR-DEBT-06); mandatory (BR-ACCOUNT-11).
    /// </summary>
    public const string CustomerPaymentTransaction = "CUSTOMER_PAYMENT_TRANSACTION";

    /// <summary>MSG-TRACE-05, the withdrawal of the origin information of a lot the trader bought, to the trader account (TRACE-04.1, BR-TRACE-07); not mandatory (BR-ACCOUNT-11).</summary>
    public const string ProvenanceWithdrawn = "PROVENANCE_WITHDRAWN";

    /// <summary>MSG-SALE-18, a correction or a cancellation of a sales invoice leaves the customer debt below zero, to the Accountant and the Owner (BR-SALE-03); not mandatory.</summary>
    public const string CustomerMoneyOwedBack = "CUSTOMER_MONEY_OWED_BACK";

    /// <summary>MSG-CLAIM-01, a new quality claim, to the Cooperative Owner (BR-CLAIM-01); not mandatory (BR-CLAIM-01 says so).</summary>
    public const string QualityClaimNew = "QUALITY_CLAIM_NEW";

    /// <summary>MSG-CLAIM-02, the response to a quality claim, to the trader account (BR-CLAIM-02); also sent by SMS or Zalo OA.</summary>
    public const string QualityClaimResponse = "QUALITY_CLAIM_RESPONSE";

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
        new(CycleSeedingCompleted, "Seeding finished", Default, [CooperativeOwner],
            NotificationOpenAccess.InZone(PermissionFunctions.CultivationCycles, PermissionAction.View)),
        new(HarvestLotRecorded, "New harvest lot", Default, [CooperativeOwner],
            NotificationOpenAccess.InZone(PermissionFunctions.Harvests, PermissionAction.View)),
        // Opens the lot, where it can be closed by hand (HARVEST-02).
        new(HarvestLotAutoCloseWarning, "Harvest lot closing automatically", Default, [CooperativeOwner, ZoneManager],
            NotificationOpenAccess.InZone(PermissionFunctions.Harvests, PermissionAction.View)),
        // Mandatory (BR-ACCOUNT-11), so it has an SMS: to the Owner only, as decided for every type but the water alert (OMTS-216, 03/11).
        new(CycleCloseReminder, "Reminder to close a cultivation cycle", AlwaysPush, [CooperativeOwner, ZoneManager],
            NotificationOpenAccess.InZone(PermissionFunctions.CultivationCycles, PermissionAction.View),
            NotificationSmsRule.MessageTextTo(CooperativeOwner)),
        // Opens the progress view of the closed cycle on its close date (CYCLE-05.2).
        new(CycleClosed, "Cultivation cycle closed", Default, [CooperativeOwner],
            NotificationOpenAccess.InZone(PermissionFunctions.CultivationCycles, PermissionAction.View)),
        // Opening a purchase receipt follows the role only, whatever its zone (BR-FARM-08). Only the transaction of the
        // Accountant (MSG-PURCHASE-04, PURCHASE-05) is mandatory in FE-08 (BR-ACCOUNT-11).
        new(PurchaseReceiptCompleted, "New purchase receipt", Default, [CooperativeOwner],
            NotificationOpenAccess.InZone(PermissionFunctions.PurchaseReceipts, PermissionAction.View)),
        new(PurchaseReceiptCancelledPaidInAdvance, "Supplier paid in advance after a receipt cancellation", Default, [CooperativeOwner, Accountant],
            NotificationOpenAccess.InZone(PermissionFunctions.PurchaseReceipts, PermissionAction.View)),
        new(SupplierTransaction, "Supplier payment transaction", AlwaysPush, [CooperativeOwner], NotificationOpenAccess.OwnerOnly,
            NotificationSmsRule.MoneyToOwner("amount")),
        new(CultivationLogRecorded, "New cultivation log", Default, [CooperativeOwner],
            NotificationOpenAccess.InZone(PermissionFunctions.CultivationLogs, PermissionAction.View)),
        // Opens the invoice (SALE-09), in the zone of the invoice (BR-SALE-08).
        new(SaleCompleted, "New sale", Default, [CooperativeOwner],
            NotificationOpenAccess.InZone(PermissionFunctions.SalesInvoices, PermissionAction.View)),
        // Customer payments have no zone (BR-FARM-08); a money type, so its SMS carries the code and the amount only.
        new(CustomerPaymentTransaction, "Customer payment transaction", AlwaysPush, [CooperativeOwner], NotificationOpenAccess.OwnerOnly,
            NotificationSmsRule.MoneyToOwner("amount")),
        // Opens nothing: the trader is asked to call the cooperative, and the list row has no related record (a lot is not the trader's record).
        new(ProvenanceWithdrawn, "Origin information withdrawn", Default, [Trader], NotificationOpenAccess.TraderOfTheRecord),
        // Opens the invoice (SALE-09); the money is paid back in DEBT-02.
        new(CustomerMoneyOwedBack, "Money owed back to a customer", Default, [CooperativeOwner, Accountant],
            NotificationOpenAccess.InZone(PermissionFunctions.SalesInvoices, PermissionAction.View)),
        // A trader asked for an account from the Zalo Mini App, and the Owner's approval (ACS-01, decision of 28/10/2026).
        new(TraderAccessRequested, "Trader access requested", Default, [CooperativeOwner], NotificationOpenAccess.OwnerOnly),
        new(TraderAccessApproved, "Trader access approved", Default, [Trader], NotificationOpenAccess.TraderOfTheRecord),
        new(QualityClaimNew, "New quality claim", Default, [CooperativeOwner], NotificationOpenAccess.OwnerOnly),
        // Opens the claim (CLAIM-02 response), for the trader the claim belongs to now.
        new(QualityClaimResponse, "Response to your quality claim", Default, [Trader], NotificationOpenAccess.TraderOfTheRecord),
    ];

    private static readonly Dictionary<string, NotificationTypeDefinition> ByCode = All.ToDictionary(t => t.Code, StringComparer.Ordinal);

    /// <summary>The registered type, or null.</summary>
    public static NotificationTypeDefinition? Find(string? code) => code is null ? null : ByCode.GetValueOrDefault(code);

    /// <summary>The types a person holding <paramref name="roles"/> can receive and that are offered in the preferences.</summary>
    public static IReadOnlyList<NotificationTypeDefinition> OfferedTo(IReadOnlySet<NotificationRole> roles) =>
        All.Where(t => t.PushPolicy != NeverPush && t.Roles.Any(roles.Contains)).ToList();
}
