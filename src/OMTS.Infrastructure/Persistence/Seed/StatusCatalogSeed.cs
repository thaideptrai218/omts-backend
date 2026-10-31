using OMTS.Domain.Entities.Platform;
using G = OMTS.Domain.Constants.StatusGroups;
using S = OMTS.Domain.Constants.StatusIds;

namespace OMTS.Infrastructure.Persistence.Seed;

/// <summary>
/// Fixed Status catalog (SRS Table 3.4-1, section 3.4.3) and permitted transitions (Table 3.4-2 plus
/// the two USAGE transitions). Codes and transitions change only with an SRS revision (BR-29, BR-30).
/// </summary>
internal static class StatusCatalogSeed
{
    private const string Owner = "Cooperative Owner";
    private const string OwnerOrZoneManager = "Cooperative Owner; Zone Manager";
    private const string OwnerForStaffAdminForTraders =
        "Cooperative Owner (staff accounts); System Administrator (trader accounts, platform administration outside the cooperative app)";
    private const string Action = "Action";

    public static readonly Status[] Statuses =
    [
        NewStatus(S.PurchaseRequest.AwaitingResponse, G.PurchaseRequest, "AWAITING_RESPONSE", "Awaiting response", "SALE-01: purchase request in whole cages, awaiting the Owner.", initial: true),
        NewStatus(S.PurchaseRequest.Accepted, G.PurchaseRequest, "ACCEPTED", "Accepted", "SALE-03: the Owner accepted a cage quantity and pickup date."),
        NewStatus(S.PurchaseRequest.Declined, G.PurchaseRequest, "DECLINED", "Declined", "SALE-03: the Owner declined with a reason.", final: true),
        NewStatus(S.PurchaseRequest.Cancelled, G.PurchaseRequest, "CANCELLED", "Cancelled", "SALE-02: the trader cancelled their own request.", final: true),
        NewStatus(S.Usage.InUse, G.Usage, "IN_USE", "In Use", "Selectable for new records.", initial: true),
        NewStatus(S.Usage.Discontinued, G.Usage, "OUT_OF_USE", "Discontinued", "Not shown when selecting new records; old data stays unchanged."),

        NewStatus(S.Cooperative.Draft, G.Cooperative, "DRAFT", "Preliminary", "Created at deployment; the Cooperative Owner has not completed the legal profile yet.", initial: true),
        NewStatus(S.Cooperative.Completed, G.Cooperative, "COMPLETED", "Completed", "The Cooperative Owner has entered all mandatory cooperative profile information."),

        NewStatus(S.Account.Active, G.Account, "ACTIVE", "Active", "Can log in according to effective zone–role pairs.", initial: true),
        NewStatus(S.Account.Locked, G.Account, "LOCKED", "Locked", "Cannot log in; data and history stay unchanged."),
        // Not in the SRS table yet: traders asking for an account from the Zalo Mini App (decision of 28/10/2026).
        NewStatus(S.Account.PendingApproval, G.Account, "PENDING_APPROVAL", "Pending approval", "A trader asked for an account from the Zalo Mini App; cannot log in until the Cooperative Owner approves.", initial: true),
        NewStatus(S.Account.Rejected, G.Account, "REJECTED", "Rejected", "The Cooperative Owner refused the request; the trader may ask again."),

        NewStatus(S.Employee.Working, G.Employee, "WORKING", "Employed", "The employee is working and can be assigned work and recorded in attendance.", initial: true),
        NewStatus(S.Employee.Left, G.Employee, "LEFT", "Left", "No new work or attendance; wage and salary history stays unchanged."),

        NewStatus(S.AccessGrant.Effective, G.AccessGrant, "EFFECTIVE", "Effective", "The account has the role's permissions in the assigned zone.", initial: true),
        NewStatus(S.AccessGrant.Revoked, G.AccessGrant, "REVOKED", "Revoked", "The grant no longer applies; re-granting creates a new grant.", final: true),

        NewStatus(S.CultivationCycle.Open, G.CultivationCycle, "OPEN", "Open", "The cycle is in progress; seeding, logging and harvesting are allowed.", initial: true),
        NewStatus(S.CultivationCycle.Closed, G.CultivationCycle, "CLOSED", "Closed", "The cycle has ended; the raft can open a new cycle.", final: true),

        NewStatus(S.WaterMeasurement.Draft, G.WaterMeasurement, "DRAFT", "Draft", "OCR or manually entered values not yet confirmed; not used for reports or alerts.", initial: true),
        NewStatus(S.WaterMeasurement.Confirmed, G.WaterMeasurement, "CONFIRMED", "Confirmed", "Checked against the photo and confirmed; used for alerts and reports."),
        NewStatus(S.WaterMeasurement.Superseded, G.WaterMeasurement, "SUPERSEDED", "Corrected", "Replaced by a new correction version; the old record is kept for reference.", final: true),
        NewStatus(S.WaterMeasurement.Voided, G.WaterMeasurement, "VOIDED", "Voided", "Recorded by mistake and voided with a reason; excluded from charts, reports, exports and the overdue check.", final: true),

        NewStatus(S.PurchaseReceipt.Draft, G.PurchaseReceipt, "DRAFT", "Draft", "Being recorded; does not count in costs, supplier debt or seed lots.", initial: true),
        NewStatus(S.PurchaseReceipt.Completed, G.PurchaseReceipt, "COMPLETED", "Completed", "Counts in input costs and supplier debt; seed lots created; never edited."),
        NewStatus(S.PurchaseReceipt.Cancelled, G.PurchaseReceipt, "CANCELLED", "Cancelled", "Recorded by mistake; leaves costs and supplier debt, its seed lots cannot be stocked.", final: true),

        NewStatus(S.SupplierPayment.Recorded, G.SupplierPayment, "RECORDED", "Recorded", "Money paid to the supplier; reduces supplier debt.", initial: true),
        NewStatus(S.SupplierPayment.Cancelled, G.SupplierPayment, "CANCELLED", "Cancelled", "Cancelled together with its purchase receipt.", final: true),
    ];

    public static readonly StatusTransition[] Transitions =
    [
        NewTransition(1, S.Usage.InUse, S.Usage.Discontinued, "UC12, UC15, UC18, UC20, UC22, UC42, UC55", OwnerOrZoneManager, Action, "Not blocked by the entity's Discontinued condition (section 3.4.3).", false),
        NewTransition(2, S.Usage.Discontinued, S.Usage.InUse, "UC12, UC15, UC18, UC20, UC22, UC42, UC55", OwnerOrZoneManager, Action, "Return the item to use.", false),

        NewTransition(3, S.Cooperative.Draft, S.Cooperative.Completed, "FARM-01", Owner, Action, "All mandatory fields of the cooperative profile are filled.", false),

        NewTransition(4, S.Account.Active, S.Account.Locked, "ACS-09", OwnerForStaffAdminForTraders, Action, "Lock the account.", true),
        NewTransition(5, S.Account.Locked, S.Account.Active, "ACS-09", OwnerForStaffAdminForTraders, Action, "Unlock after verification; trader accounts are unlocked only by the System Administrator.", true),

        NewTransition(6, S.Employee.Working, S.Employee.Left, "UC10", Owner, Action, "The employee leaves.", false),
        NewTransition(7, S.Employee.Left, S.Employee.Working, "UC10", Owner, Action, "The employee returns to work.", false),

        NewTransition(8, S.AccessGrant.Effective, S.AccessGrant.Revoked, "UC11", Owner, Action, "Revoke the zone–role pair; Cooperative Owner only.", true),

        NewTransition(9, S.CultivationCycle.Open, S.CultivationCycle.Closed, "UC27", OwnerOrZoneManager, Action, "The seeded portion has been fully harvested or written off (BR-21).", false),

        NewTransition(10, S.WaterMeasurement.Draft, S.WaterMeasurement.Confirmed, "UC26", OwnerOrZoneManager, Action, "Check against the photo and confirm the values.", false),
        NewTransition(11, S.WaterMeasurement.Confirmed, S.WaterMeasurement.Superseded, "UC26", OwnerOrZoneManager, Action, "Save a new correction version.", true),

        // 12–54 were the transitions of the removed SRS 22/10 groups (OMTS-216); their ids are not reused.
        NewTransition(55, S.WaterMeasurement.Draft, S.WaterMeasurement.Voided, "ENV-02", OwnerOrZoneManager, Action, "Void a wrong draft.", true),
        NewTransition(56, S.WaterMeasurement.Confirmed, S.WaterMeasurement.Voided, "ENV-02", OwnerOrZoneManager, Action, "Void the latest version of a wrong measurement.", true),

        NewTransition(70, S.PurchaseReceipt.Draft, S.PurchaseReceipt.Completed, "INP-03", OwnerOrZoneManager, Action, "Full validation of supplier, zone, raft, lines and amounts passes.", false),
        NewTransition(71, S.PurchaseReceipt.Draft, S.PurchaseReceipt.Cancelled, "INP-04", OwnerOrZoneManager, Action, "Cancel a draft that will not be completed.", true),
        NewTransition(72, S.PurchaseReceipt.Completed, S.PurchaseReceipt.Cancelled, "INP-04", OwnerOrZoneManager, Action, "No seed lot of the receipt is stocked and the receipt has no purchase return.", true),
        NewTransition(73, S.SupplierPayment.Recorded, S.SupplierPayment.Cancelled, "INP-04", OwnerOrZoneManager, Action, "The receipt is cancelled together with its payments.", true),

        NewTransition(77, S.PurchaseRequest.AwaitingResponse, S.PurchaseRequest.Accepted, "SALE-03", Owner, Action, "Positive accepted cage count no larger than requested; pickup date is today or later.", false),
        NewTransition(78, S.PurchaseRequest.AwaitingResponse, S.PurchaseRequest.Declined, "SALE-03", Owner, Action, "The Owner enters a reason.", true),
        NewTransition(79, S.PurchaseRequest.AwaitingResponse, S.PurchaseRequest.Cancelled, "SALE-02", "Trader", Action, "Only the requesting customer may cancel.", false),
        NewTransition(80, S.PurchaseRequest.Accepted, S.PurchaseRequest.Cancelled, "SALE-02", "Trader", Action, "The request is not fulfilled; only the requesting customer may cancel.", false),
        // Trader accounts requested from the Zalo Mini App (not in the SRS table yet, decision of 28/10/2026).
        NewTransition(74, S.Account.PendingApproval, S.Account.Active, "ACS-01 (Zalo)", Owner, Action, "Approve the trader's request.", false),
        NewTransition(75, S.Account.PendingApproval, S.Account.Rejected, "ACS-01 (Zalo)", Owner, Action, "Refuse the trader's request.", true),
        NewTransition(76, S.Account.Rejected, S.Account.PendingApproval, "ACS-01 (Zalo)", "Trader", Action, "The trader asks again from the Zalo Mini App.", false),
    ];

    private static Status NewStatus(int id, string group, string code, string displayName, string meaning, bool initial = false, bool final = false) =>
        new()
        {
            Id = id,
            Group = group,
            Code = code,
            DisplayName = displayName,
            Meaning = meaning,
            IsInitial = initial,
            IsFinal = final,
            SortOrder = id % 100,
        };

    private static StatusTransition NewTransition(int id, int from, int to, string useCases, string performers, string trigger, string guard, bool reasonRequired) =>
        new()
        {
            Id = id,
            FromStatusId = from,
            ToStatusId = to,
            UseCases = useCases,
            AllowedPerformers = performers,
            TriggerEvent = trigger,
            GuardCondition = guard,
            ReasonRequired = reasonRequired,
        };
}
