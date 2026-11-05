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
    private const string Trader = "Trader";
    private const string SystemActor = "System";
    private const string Action = "Action";

    public static readonly Status[] Statuses =
    [
        NewStatus(S.Usage.InUse, G.Usage, "IN_USE", "In Use", "Selectable for new records.", initial: true),
        NewStatus(S.Usage.Discontinued, G.Usage, "OUT_OF_USE", "Discontinued", "Not shown when selecting new records; old data stays unchanged."),

        NewStatus(S.Account.Active, G.Account, "ACTIVE", "Active", "Can log in according to effective zone–role pairs.", initial: true),
        NewStatus(S.Account.Locked, G.Account, "LOCKED", "Locked", "Cannot log in; data and history stay unchanged."),

        NewStatus(S.Employee.Working, G.Employee, "WORKING", "Employed", "The employee is working and can be assigned work and recorded in attendance.", initial: true),
        NewStatus(S.Employee.Left, G.Employee, "LEFT", "Left", "No new work or attendance; wage and salary history stays unchanged."),

        NewStatus(S.AccessGrant.Effective, G.AccessGrant, "EFFECTIVE", "Effective", "The account has the role's permissions in the assigned zone.", initial: true),
        NewStatus(S.AccessGrant.Revoked, G.AccessGrant, "REVOKED", "Revoked", "The grant no longer applies; re-granting creates a new grant.", final: true),

        NewStatus(S.CultivationCycle.Open, G.CultivationCycle, "OPEN", "Open", "The cycle is in progress; seeding, logging and harvesting are allowed.", initial: true),
        NewStatus(S.CultivationCycle.Closed, G.CultivationCycle, "CLOSED", "Closed", "The cycle has ended; the raft can open a new cycle.", final: true),

        NewStatus(S.WaterMeasurement.Active, G.WaterMeasurement, "ACTIVE", "Active", "Counts in the charts and the water alert of the zone.", initial: true),
        NewStatus(S.WaterMeasurement.Voided, G.WaterMeasurement, "VOIDED", "Voided", "Kept in the history with its reason; no longer counts.", final: true),

        // SMS and Zalo OA sendings kept as Notification rows (BR-SALE-09, GFR-06).
        NewStatus(S.MessageDelivery.Sending, G.MessageDelivery, "SENDING", "Sending", "Handed to the SMS Service or Zalo OA, no answer yet.", initial: true),
        NewStatus(S.MessageDelivery.Sent, G.MessageDelivery, "SENT", "Sent", "Accepted by the service; this does not mean that the trader has read it.", final: true),
        NewStatus(S.MessageDelivery.NotSent, G.MessageDelivery, "NOT_SENT", "Not sent", "Not accepted by the service; nothing that was saved is undone (GFR-06).", final: true),

        NewStatus(S.PurchaseReceipt.Draft, G.PurchaseReceipt, "DRAFT", "Draft", "Being prepared; counts nowhere and creates no seed lot. Removed, never cancelled.", initial: true),
        NewStatus(S.PurchaseReceipt.Completed, G.PurchaseReceipt, "COMPLETED", "Completed", "Cannot be changed; counts in the supplier debt and raft material costs; each Seed line is a seed lot."),
        NewStatus(S.PurchaseReceipt.Cancelled, G.PurchaseReceipt, "CANCELLED", "Cancelled", "Kept with its reason; counts nowhere; its Seed lines are no longer seed lots.", final: true),

        NewStatus(S.SupplierPayment.Recorded, G.SupplierPayment, "RECORDED", "Recorded", "Money paid to the supplier; reduces supplier debt.", initial: true),
        NewStatus(S.SupplierPayment.Cancelled, G.SupplierPayment, "CANCELLED", "Cancelled", "Recorded by mistake; no longer counts in the supplier debt.", final: true),

        // STD-PURCHASE-REQUEST. Fulfilled is worked out from the invoices, so it has no row (BR-SALE-01).
        NewStatus(S.PurchaseRequest.AwaitingResponse, G.PurchaseRequest, "AWAITING_RESPONSE", "Awaiting response", "Sent by the trader; not answered yet.", initial: true),
        NewStatus(S.PurchaseRequest.Accepted, G.PurchaseRequest, "ACCEPTED", "Accepted", "Accepted by the Cooperative Owner, possibly for fewer kg; invoices may be linked to it."),
        NewStatus(S.PurchaseRequest.Declined, G.PurchaseRequest, "DECLINED", "Declined", "Declined by the Cooperative Owner with a note.", final: true),
        NewStatus(S.PurchaseRequest.Cancelled, G.PurchaseRequest, "CANCELLED", "Cancelled", "Cancelled by the trader; no invoice can be linked to it.", final: true),
    ];

    public static readonly StatusTransition[] Transitions =
    [
        NewTransition(1, S.Usage.InUse, S.Usage.Discontinued, "UC12, UC15, UC18, UC20, UC22, UC42, UC55", OwnerOrZoneManager, Action, "Not blocked by the entity's Discontinued condition (section 3.4.3).", false),
        NewTransition(2, S.Usage.Discontinued, S.Usage.InUse, "UC12, UC15, UC18, UC20, UC22, UC42, UC55", OwnerOrZoneManager, Action, "Return the item to use.", false),

        // Id 3 (cooperative Preliminary → Completed) is no longer used: the cooperative has no status (SRS 27/10).

        NewTransition(4, S.Account.Active, S.Account.Locked, "ACS-09", OwnerForStaffAdminForTraders, Action, "Lock the account.", true),
        NewTransition(5, S.Account.Locked, S.Account.Active, "ACS-09", OwnerForStaffAdminForTraders, Action, "Unlock after verification; trader accounts are unlocked only by the System Administrator.", true),

        NewTransition(6, S.Employee.Working, S.Employee.Left, "LABOR-01", $"{OwnerOrZoneManager}; {SystemActor}", Action, "The last working day has passed: at once for an earlier day, by the system on the next day for today (BR-LABOR-01).", false),
        NewTransition(7, S.Employee.Left, S.Employee.Working, "LABOR-01", OwnerOrZoneManager, Action, "No Employed employee has the phone number; needs no new approval (BR-LABOR-01).", false),

        NewTransition(8, S.AccessGrant.Effective, S.AccessGrant.Revoked, "UC11", Owner, Action, "Revoke the zone–role pair; Cooperative Owner only.", true),

        NewTransition(9, S.CultivationCycle.Open, S.CultivationCycle.Closed, "UC27", OwnerOrZoneManager, Action, "The seeded portion has been fully harvested or written off (BR-21).", false),

        // 10–11, 55–56 were the water transitions of the SRS 22/10 model (OMTS-219); their ids are not reused.
        // 12–52 were the transitions of the removed SRS 22/10 groups (OMTS-216); their ids are not reused.
        NewTransition(53, S.MessageDelivery.Sending, S.MessageDelivery.Sent, "SALE-06, SALE-03, CLAIM-02, TRACE-04", SystemActor, SystemActor, "The SMS Service or Zalo OA accepts the message.", false),
        NewTransition(54, S.MessageDelivery.Sending, S.MessageDelivery.NotSent, "SALE-06, SALE-03, CLAIM-02, TRACE-04", SystemActor, SystemActor, "The SMS Service or Zalo OA does not accept the message.", false),

        NewTransition(80, S.WaterMeasurement.Active, S.WaterMeasurement.Voided, "WATER-02", "Zone Manager (own); Cooperative Owner", Action, "Void a wrong measurement with a reason.", true),

        // A Draft receipt is removed, never cancelled (BR-PURCHASE-09): transition 71 no longer exists.
        NewTransition(70, S.PurchaseReceipt.Draft, S.PurchaseReceipt.Completed, "PURCHASE-01", "Zone Manager (own drafts); Cooperative Owner", Action, "Supplier In Use, date received not later than today, and every line complete with an input item In Use, a quantity greater than 0 and a unit price of 0 or more.", false),
        NewTransition(72, S.PurchaseReceipt.Completed, S.PurchaseReceipt.Cancelled, "PURCHASE-06", "Zone Manager (own receipts); Cooperative Owner", Action, "No seed lot of the receipt has been seeded and the receipt has no purchase return that is not cancelled.", true),
        NewTransition(73, S.SupplierPayment.Recorded, S.SupplierPayment.Cancelled, "PURCHASE-05", "Accountant; Cooperative Owner", Action, "A payment recorded by mistake; payments are never cancelled with a purchase receipt.", true),

        NewTransition(180, S.PurchaseRequest.AwaitingResponse, S.PurchaseRequest.Accepted, "SALE-03", Owner, Action, "The accepted kg is above 0 and not more than the requested kg; the pickup date is not before today.", false),
        NewTransition(181, S.PurchaseRequest.AwaitingResponse, S.PurchaseRequest.Declined, "SALE-03", Owner, Action, "A note gives the reason.", true),
        NewTransition(182, S.PurchaseRequest.AwaitingResponse, S.PurchaseRequest.Cancelled, "SALE-02", Trader, Action, "The trader cancels; a reason is optional.", false),
        NewTransition(183, S.PurchaseRequest.Accepted, S.PurchaseRequest.Cancelled, "SALE-02", Trader, Action, "The request is not Fulfilled; a reason is optional.", false),
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
