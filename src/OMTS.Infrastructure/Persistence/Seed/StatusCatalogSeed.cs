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
    private const string SystemActor = "System";
    private const string Action = "Action";

    public static readonly Status[] Statuses =
    [
        NewStatus(S.Usage.InUse, G.Usage, "IN_USE", "In Use", "Selectable for new records.", initial: true),
        NewStatus(S.Usage.Discontinued, G.Usage, "OUT_OF_USE", "Discontinued", "Not shown when selecting new records; old data stays unchanged."),

        NewStatus(S.Cooperative.Draft, G.Cooperative, "DRAFT", "Preliminary", "Created at deployment; the Cooperative Owner has not completed the legal profile yet.", initial: true),
        NewStatus(S.Cooperative.Completed, G.Cooperative, "COMPLETED", "Completed", "The Cooperative Owner has entered all mandatory cooperative profile information."),

        NewStatus(S.Account.Active, G.Account, "ACTIVE", "Active", "Can log in according to effective zone–role pairs.", initial: true),
        NewStatus(S.Account.Locked, G.Account, "LOCKED", "Locked", "Cannot log in; data and history stay unchanged."),

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

        NewStatus(S.SalesOrder.PendingReview, G.SalesOrder, "PENDING_REVIEW", "Pending Review", "The purchase request has just been sent and not yet reviewed by the cooperative.", initial: true),
        NewStatus(S.SalesOrder.AwaitingCustomer, G.SalesOrder, "AWAITING_CUSTOMER", "Awaiting Customer Agreement", "The cooperative proposes a change of quantity or time, awaiting the trader's agreement."),
        NewStatus(S.SalesOrder.Confirmed, G.SalesOrder, "CONFIRMED", "Confirmed", "The cooperative has confirmed the product, quantity and order zone; the price is not yet agreed."),
        NewStatus(S.SalesOrder.Harvesting, G.SalesOrder, "HARVESTING", "Harvesting", "A harvest plan or a first harvest exists for the order."),
        NewStatus(S.SalesOrder.Harvested, G.SalesOrder, "HARVESTED", "Harvested", "The actual harvest has been confirmed and the traceability lot created, or the order took over the lot of a cancelled order."),
        NewStatus(S.SalesOrder.HandedOver, G.SalesOrder, "HANDED_OVER", "Handed Over", "The goods have been weighed and handed over at the port."),
        NewStatus(S.SalesOrder.Sold, G.SalesOrder, "SOLD", "Sale Finalized", "The sale transaction has an agreed price and weight."),
        NewStatus(S.SalesOrder.ReturnRequested, G.SalesOrder, "RETURN_REQUESTED", "Return Requested", "The trader has requested return of the whole received order, awaiting the Cooperative Owner's decision."),
        NewStatus(S.SalesOrder.ReturnAccepted, G.SalesOrder, "RETURN_ACCEPTED", "Return Accepted", "The Cooperative Owner accepted return of the whole order, awaiting the goods."),
        NewStatus(S.SalesOrder.Returned, G.SalesOrder, "RETURNED", "Return Received", "The order's returned goods have arrived and been recorded.", final: true),
        NewStatus(S.SalesOrder.Rejected, G.SalesOrder, "REJECTED", "Rejected", "The cooperative rejected, or the trader did not agree to the proposed changes.", final: true),
        NewStatus(S.SalesOrder.Cancelled, G.SalesOrder, "CANCELLED", "Cancelled", "The trader withdrew the request or cancelled the order before handover.", final: true),

        NewStatus(S.HarvestEvent.Draft, G.HarvestEvent, "DRAFT", "Draft", "Lane portions and estimates are being recorded during the day.", initial: true),
        NewStatus(S.HarvestEvent.Confirmed, G.HarvestEvent, "CONFIRMED", "Confirmed", "Confirmed at the end of the day; later edits go through correction.", final: true),

        NewStatus(S.TracePublication.Draft, G.TracePublication, "DRAFT", "Draft", "The traceability record is being prepared or the review has not passed.", initial: true),
        NewStatus(S.TracePublication.Passed, G.TracePublication, "PASSED", "Passed", "Review passed, awaiting publication."),
        NewStatus(S.TracePublication.Published, G.TracePublication, "PUBLISHED", "Published", "Currently shown on the public QR page."),
        NewStatus(S.TracePublication.NeedsReview, G.TracePublication, "NEEDS_REVIEW", "Needs Re-review", "Published data has just been corrected; no handover, resale or portal synchronization."),
        NewStatus(S.TracePublication.Superseded, G.TracePublication, "SUPERSEDED", "Superseded", "Replaced by a newly published version.", final: true),
        NewStatus(S.TracePublication.Withdrawn, G.TracePublication, "WITHDRAWN", "Withdrawn", "Publication withdrawn; the QR page shows the withdrawn status.", final: true),

        NewStatus(S.QualityClaim.Pending, G.QualityClaim, "PENDING", "Awaiting Decision", "The claim has been submitted; the Cooperative Owner has not decided yet.", initial: true),
        NewStatus(S.QualityClaim.InProgress, G.QualityClaim, "IN_PROGRESS", "In Progress", "A discount or return of the whole order has been decided and is being carried out."),
        NewStatus(S.QualityClaim.Closed, G.QualityClaim, "CLOSED", "Closed", "Rejected, discount recorded, returned goods resolved, or return acceptance cancelled.", final: true),

        NewStatus(S.PhysicalReturn.Holding, G.PhysicalReturn, "HOLDING", "In Temporary Holding", "The returned goods have arrived and are held awaiting assessment.", initial: true),
        NewStatus(S.PhysicalReturn.Assessed, G.PhysicalReturn, "ASSESSED", "Assessed", "The resellable portion and the portion for other disposal have been assessed."),
        NewStatus(S.PhysicalReturn.Disposed, G.PhysicalReturn, "DISPOSED", "Resolved", "The resellable portion is linked to new orders and the rest otherwise disposed of.", final: true),

        NewStatus(S.SaleAdjustment.Draft, G.SaleAdjustment, "DRAFT", "Draft", "Awaiting the Cooperative Owner's approval; no effect on debt yet.", initial: true),
        NewStatus(S.SaleAdjustment.Approved, G.SaleAdjustment, "APPROVED", "Approved", "Approved; changes the receivable or records a credit/amount to refund.", final: true),
        NewStatus(S.SaleAdjustment.Rejected, G.SaleAdjustment, "REJECTED", "Rejected", "Not approved; debt unchanged.", final: true),

        NewStatus(S.PayRule.Draft, G.PayRule, "DRAFT", "Draft", "A new salary or wage rate awaiting approval; not yet used for calculation.", initial: true),
        NewStatus(S.PayRule.Approved, G.PayRule, "APPROVED", "Approved", "Approved; used from the effective date.", final: true),
        NewStatus(S.PayRule.Rejected, G.PayRule, "REJECTED", "Rejected", "Not approved; the current rate stays unchanged.", final: true),

        NewStatus(S.LaborSettlement.Draft, G.LaborSettlement, "DRAFT", "Draft", "The timesheet or payroll is being prepared, awaiting approval.", initial: true),
        NewStatus(S.LaborSettlement.Approved, G.LaborSettlement, "APPROVED", "Approved", "Approved by the Cooperative Owner; the basis for payment."),
        NewStatus(S.LaborSettlement.Paid, G.LaborSettlement, "PAID", "Fully Paid", "Total paid equals the approved obligation.", final: true),

        NewStatus(S.MessageDelivery.Sending, G.MessageDelivery, "SENDING", "Sending", "Handed to the channel provider, no response yet.", initial: true),
        NewStatus(S.MessageDelivery.Sent, G.MessageDelivery, "SENT", "Sent", "The provider reports successful delivery.", final: true),
        NewStatus(S.MessageDelivery.Failed, G.MessageDelivery, "FAILED", "Failed", "The provider reports an error or the response timeout expires.", final: true),

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

        NewTransition(12, S.SalesOrder.PendingReview, S.SalesOrder.Confirmed, "UC44", Owner, Action, "Confirm the product, quantity and order zone.", false),
        NewTransition(13, S.SalesOrder.PendingReview, S.SalesOrder.AwaitingCustomer, "UC44", Owner, Action, "Propose a change of quantity or time.", false),
        NewTransition(14, S.SalesOrder.PendingReview, S.SalesOrder.Rejected, "UC44", Owner, Action, "The cooperative cannot fulfill.", true),
        NewTransition(15, S.SalesOrder.PendingReview, S.SalesOrder.Cancelled, "UC44", Owner, Action, "The trader withdraws the purchase request before review.", true),
        NewTransition(16, S.SalesOrder.AwaitingCustomer, S.SalesOrder.Confirmed, "UC44", Owner, Action, "The trader agrees; channel, time and basis of agreement recorded.", false),
        NewTransition(17, S.SalesOrder.AwaitingCustomer, S.SalesOrder.Rejected, "UC44", Owner, Action, "The trader does not agree.", true),
        NewTransition(18, S.SalesOrder.AwaitingCustomer, S.SalesOrder.Cancelled, "UC44", Owner, Action, "The trader withdraws the request while changes await agreement.", true),
        NewTransition(19, S.SalesOrder.Confirmed, S.SalesOrder.Cancelled, "UC44", Owner, Action, "The trader cancels the order.", true),
        NewTransition(20, S.SalesOrder.Confirmed, S.SalesOrder.Harvesting, "UC30, UC31", OwnerOrZoneManager, Action, "Plan the harvest, or record the first harvest when no plan is made.", false),
        NewTransition(21, S.SalesOrder.Harvesting, S.SalesOrder.Harvested, "UC31", OwnerOrZoneManager, Action, "Harvest Done selected; every harvest of the order is confirmed.", false),
        NewTransition(22, S.SalesOrder.Harvesting, S.SalesOrder.Cancelled, "UC44", Owner, Action, "The trader drops the order during harvest.", true),
        NewTransition(23, S.SalesOrder.Harvested, S.SalesOrder.Cancelled, "UC44", Owner, Action, "The trader drops the order after harvest, before handover.", true),
        NewTransition(24, S.SalesOrder.Harvested, S.SalesOrder.HandedOver, "UC45", OwnerOrZoneManager, Action, "Weigh and hand over at the port.", false),
        NewTransition(25, S.SalesOrder.Confirmed, S.SalesOrder.HandedOver, "UC45", OwnerOrZoneManager, Action, "Resale order of returned goods: no harvest involved.", false),
        NewTransition(26, S.SalesOrder.Confirmed, S.SalesOrder.Harvested, "UC44", Owner, Action, "An order without a lot takes over the harvested lot of a cancelled order of the same product.", false),
        NewTransition(27, S.SalesOrder.HandedOver, S.SalesOrder.Sold, "UC48", OwnerOrZoneManager, Action, "Agree the price and weight.", false),
        NewTransition(28, S.SalesOrder.Sold, S.SalesOrder.ReturnRequested, "UC51", "Trader; Cooperative Owner", Action, "Within 24 hours of sale finalization; no Draft/Approved discount and no return request yet.", false),
        NewTransition(29, S.SalesOrder.ReturnRequested, S.SalesOrder.ReturnAccepted, "UC52", Owner, Action, "Accept return of the whole order; the trader is notified.", false),
        NewTransition(30, S.SalesOrder.ReturnRequested, S.SalesOrder.Sold, "UC52", Owner, Action, "Reject the return or decide on a discount instead.", true),
        NewTransition(31, S.SalesOrder.ReturnAccepted, S.SalesOrder.Returned, "UC52", OwnerOrZoneManager, Action, "The returned goods have arrived and are recorded as received.", false),
        NewTransition(32, S.SalesOrder.ReturnAccepted, S.SalesOrder.Sold, "UC52", Owner, Action, "Trader unreachable or goods not returned: cancel the return acceptance.", true),

        NewTransition(33, S.HarvestEvent.Draft, S.HarvestEvent.Confirmed, "UC31", OwnerOrZoneManager, Action, "Confirm at the end of the harvest day.", false),

        NewTransition(34, S.TracePublication.Draft, S.TracePublication.Passed, "UC32", OwnerOrZoneManager, Action, "Review passed.", false),
        NewTransition(35, S.TracePublication.Passed, S.TracePublication.Published, "UC32", OwnerOrZoneManager, Action, "Publish the passed version.", false),
        NewTransition(36, S.TracePublication.Passed, S.TracePublication.Draft, "UC32", SystemActor, SystemActor, "The lot's source data changed after review and before publication.", false),
        NewTransition(37, S.TracePublication.Published, S.TracePublication.NeedsReview, "UC39", SystemActor, SystemActor, "A correction of source data or weight affects published content.", false),
        NewTransition(38, S.TracePublication.Published, S.TracePublication.Withdrawn, "UC32", OwnerOrZoneManager, Action, "Published weight or source proven wrong, or the lot can no longer be used.", true),
        NewTransition(39, S.TracePublication.NeedsReview, S.TracePublication.Superseded, "UC32", SystemActor, SystemActor, "A new version of the same lot is published.", false),
        NewTransition(40, S.TracePublication.NeedsReview, S.TracePublication.Withdrawn, "UC32", OwnerOrZoneManager, Action, "Published weight or source proven wrong, or the lot can no longer be used.", true),

        NewTransition(41, S.QualityClaim.Pending, S.QualityClaim.Closed, "UC52", Owner, Action, "Reject the claim.", true),
        NewTransition(42, S.QualityClaim.Pending, S.QualityClaim.InProgress, "UC52", Owner, Action, "Decide on a discount, or accept return of the whole order.", false),
        NewTransition(43, S.QualityClaim.InProgress, S.QualityClaim.Closed, "UC53, UC52", SystemActor, SystemActor, "Discount approved/rejected, returned goods resolved, or return acceptance cancelled.", false),

        NewTransition(44, S.PhysicalReturn.Holding, S.PhysicalReturn.Assessed, "UC52", Owner, Action, "Assess the resellable portion.", false),
        NewTransition(45, S.PhysicalReturn.Assessed, S.PhysicalReturn.Disposed, "UC52", SystemActor, SystemActor, "All resold or other disposal recorded.", false),

        NewTransition(46, S.SaleAdjustment.Draft, S.SaleAdjustment.Approved, "UC53, UC39", Owner, Action, "The total decrease does not exceed the remaining adjustable amount.", false),
        NewTransition(47, S.SaleAdjustment.Draft, S.SaleAdjustment.Rejected, "UC53, UC39", Owner, Action, "The Cooperative Owner does not approve the adjustment.", true),

        NewTransition(48, S.PayRule.Draft, S.PayRule.Approved, "UC58", Owner, Action, "The effective period does not overlap an approved rate of the same subject.", false),
        NewTransition(49, S.PayRule.Draft, S.PayRule.Rejected, "UC58", Owner, Action, "The Cooperative Owner does not approve the new rate.", true),

        NewTransition(50, S.LaborSettlement.Draft, S.LaborSettlement.Approved, "UC60", Owner, Action, "Review and approve the sheet.", false),
        NewTransition(51, S.LaborSettlement.Approved, S.LaborSettlement.Draft, "UC60", Owner, Action, "Reopen for editing; audit log written (before – after).", true),
        NewTransition(52, S.LaborSettlement.Approved, S.LaborSettlement.Paid, "UC61", SystemActor, SystemActor, "Total paid equals the approved obligation.", false),

        NewTransition(53, S.MessageDelivery.Sending, S.MessageDelivery.Sent, "UC70", SystemActor, SystemActor, "Success response from Zalo OA, Email Service or SMS Service.", false),
        NewTransition(54, S.MessageDelivery.Sending, S.MessageDelivery.Failed, "UC70", SystemActor, "System; time", "Error response or timeout.", false),

        NewTransition(55, S.WaterMeasurement.Draft, S.WaterMeasurement.Voided, "ENV-02", OwnerOrZoneManager, Action, "Void a wrong draft.", true),
        NewTransition(56, S.WaterMeasurement.Confirmed, S.WaterMeasurement.Voided, "ENV-02", OwnerOrZoneManager, Action, "Void the latest version of a wrong measurement.", true),

        NewTransition(70, S.PurchaseReceipt.Draft, S.PurchaseReceipt.Completed, "INP-03", OwnerOrZoneManager, Action, "Full validation of supplier, zone, raft, lines and amounts passes.", false),
        NewTransition(71, S.PurchaseReceipt.Draft, S.PurchaseReceipt.Cancelled, "INP-04", OwnerOrZoneManager, Action, "Cancel a draft that will not be completed.", true),
        NewTransition(72, S.PurchaseReceipt.Completed, S.PurchaseReceipt.Cancelled, "INP-04", OwnerOrZoneManager, Action, "No seed lot of the receipt is stocked and the receipt has no purchase return.", true),
        NewTransition(73, S.SupplierPayment.Recorded, S.SupplierPayment.Cancelled, "INP-04", OwnerOrZoneManager, Action, "The receipt is cancelled together with its payments.", true),
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
