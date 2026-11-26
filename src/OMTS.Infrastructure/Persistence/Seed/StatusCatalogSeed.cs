using OMTS.Domain.Entities.Platform;
using G = OMTS.Domain.Constants.StatusGroups;
using S = OMTS.Domain.Constants.StatusIds;

namespace OMTS.Infrastructure.Persistence.Seed;

/// <summary>
/// Fixed Status catalog (SRS Table 3.4-1, section 3.4.3). Codes change only with an SRS revision (BR-29). The moves between
/// statuses are enforced by the rules of each feature, not by the catalog.
/// </summary>
internal static class StatusCatalogSeed
{
    public static readonly Status[] Statuses =
    [
        NewStatus(S.Usage.InUse, G.Usage, "IN_USE", "In Use"),
        NewStatus(S.Usage.Discontinued, G.Usage, "OUT_OF_USE", "Discontinued"),

        NewStatus(S.Account.Active, G.Account, "ACTIVE", "Active"),
        NewStatus(S.Account.Locked, G.Account, "LOCKED", "Locked"),
        // Not in the SRS table yet: traders asking for an account from the Zalo Mini App (decision of 28/10/2026).
        NewStatus(S.Account.PendingApproval, G.Account, "PENDING_APPROVAL", "Pending approval"),
        NewStatus(S.Account.Rejected, G.Account, "REJECTED", "Rejected"),
        // Soft delete of a staff account by the Owner (decision of 26/11/2026); final: no way back.
        NewStatus(S.Account.Deleted, G.Account, "DELETED", "Deleted", final: true),

        NewStatus(S.Employee.Working, G.Employee, "WORKING", "Employed"),
        NewStatus(S.Employee.Left, G.Employee, "LEFT", "Left"),

        NewStatus(S.AccessGrant.Effective, G.AccessGrant, "EFFECTIVE", "Effective"),
        NewStatus(S.AccessGrant.Revoked, G.AccessGrant, "REVOKED", "Revoked", final: true),

        NewStatus(S.CultivationCycle.Open, G.CultivationCycle, "OPEN", "Open"),
        NewStatus(S.CultivationCycle.Closed, G.CultivationCycle, "CLOSED", "Closed", final: true),

        NewStatus(S.WaterMeasurement.Active, G.WaterMeasurement, "ACTIVE", "Active"),
        NewStatus(S.WaterMeasurement.Voided, G.WaterMeasurement, "VOIDED", "Voided", final: true),

        // SMS and Zalo OA sendings kept as Notification rows (BR-SALE-09, GFR-06).
        NewStatus(S.MessageDelivery.Sending, G.MessageDelivery, "SENDING", "Sending"),
        NewStatus(S.MessageDelivery.Sent, G.MessageDelivery, "SENT", "Sent", final: true),
        NewStatus(S.MessageDelivery.NotSent, G.MessageDelivery, "NOT_SENT", "Not sent", final: true),

        NewStatus(S.PurchaseReceipt.Draft, G.PurchaseReceipt, "DRAFT", "Draft"),
        NewStatus(S.PurchaseReceipt.Completed, G.PurchaseReceipt, "COMPLETED", "Completed"),
        NewStatus(S.PurchaseReceipt.Cancelled, G.PurchaseReceipt, "CANCELLED", "Cancelled", final: true),

        NewStatus(S.SupplierPayment.Recorded, G.SupplierPayment, "RECORDED", "Recorded"),
        NewStatus(S.SupplierPayment.Cancelled, G.SupplierPayment, "CANCELLED", "Cancelled", final: true),

        NewStatus(S.PurchaseReturn.Recorded, G.PurchaseReturn, "RECORDED", "Recorded"),
        NewStatus(S.PurchaseReturn.Cancelled, G.PurchaseReturn, "CANCELLED", "Cancelled", final: true),
        NewStatus(S.PurchaseReturn.Draft, G.PurchaseReturn, "DRAFT", "Draft"),

        NewStatus(S.Lane.InUse, G.Lane, "IN_USE", "In Use"),
        NewStatus(S.Lane.Removed, G.Lane, "REMOVED", "Removed"),
        // STD-PURCHASE-REQUEST. Fulfilled is worked out from the invoices, so it has no row (BR-SALE-01).
        NewStatus(S.PurchaseRequest.AwaitingResponse, G.PurchaseRequest, "AWAITING_RESPONSE", "Awaiting response"),
        NewStatus(S.PurchaseRequest.Accepted, G.PurchaseRequest, "ACCEPTED", "Accepted"),
        NewStatus(S.PurchaseRequest.Declined, G.PurchaseRequest, "DECLINED", "Declined", final: true),
        NewStatus(S.PurchaseRequest.Cancelled, G.PurchaseRequest, "CANCELLED", "Cancelled", final: true),
        NewStatus(S.PurchaseRequest.Ordered, G.PurchaseRequest, "ORDERED", "Ordered", final: true),

        // STD-QUALITY-CLAIM: a response cannot be changed (BR-CLAIM-02).
        NewStatus(S.QualityClaim.AwaitingResponse, G.QualityClaim, "AWAITING_RESPONSE", "Awaiting response"),
        NewStatus(S.QualityClaim.Rejected, G.QualityClaim, "REJECTED", "Rejected", final: true),
        NewStatus(S.QualityClaim.Accepted, G.QualityClaim, "ACCEPTED", "Accepted", final: true),
        NewStatus(S.QualityClaim.OfferMade, G.QualityClaim, "OFFER_MADE", "Offer made"),
        NewStatus(S.QualityClaim.OfferDeclined, G.QualityClaim, "OFFER_DECLINED", "Offer declined", final: true),

        NewStatus(S.Payroll.Draft, G.Payroll, "DRAFT", "Draft"),
        NewStatus(S.Payroll.Finalized, G.Payroll, "FINALIZED", "Finalized", final: true),
        // The web contract's Creating and Cancelled (PAYROLL_API_CONTRACT); its Provisional is the Draft above.
        NewStatus(S.Payroll.Creating, G.Payroll, "CREATING", "Creating"),
        NewStatus(S.Payroll.Cancelled, G.Payroll, "CANCELLED", "Cancelled", final: true),

        // STD-EMPLOYEE-REQUEST (BR-LABOR-08): a reviewed or cancelled request does not change any more.
        NewStatus(S.EmployeeRequest.AwaitingReview, G.EmployeeRequest, "AWAITING_REVIEW", "Awaiting review"),
        NewStatus(S.EmployeeRequest.Approved, G.EmployeeRequest, "APPROVED", "Approved", final: true),
        NewStatus(S.EmployeeRequest.Rejected, G.EmployeeRequest, "REJECTED", "Rejected", final: true),
        NewStatus(S.EmployeeRequest.Cancelled, G.EmployeeRequest, "CANCELLED", "Cancelled", final: true),

        // Chờ cân: a new lot is recorded here and is not for sale until its weighing (decision of 17/11/2026).
        NewStatus(S.HarvestLot.AwaitingWeighing, G.HarvestLot, "AWAITING_WEIGHING", "Awaiting weighing"),
        NewStatus(S.HarvestLot.Open, G.HarvestLot, "OPEN", "Open"),
        NewStatus(S.HarvestLot.Reopened, G.HarvestLot, "REOPENED", "Reopened"),
        NewStatus(S.HarvestLot.Closed, G.HarvestLot, "CLOSED", "Closed"),

        // STD-SALES-INVOICE: no draft, as an invoice is saved only when paid or put on the debt (BR-SALE-08).
        NewStatus(S.SalesInvoice.Completed, G.SalesInvoice, "COMPLETED", "Completed"),
        NewStatus(S.SalesInvoice.Cancelled, G.SalesInvoice, "CANCELLED", "Cancelled", final: true),

        // STD-CUSTOMER-PAYMENT (BR-DEBT-03): a payment counts in the debt from its recording.
        NewStatus(S.CustomerPayment.AwaitingConfirmation, G.CustomerPayment, "AWAITING_CONFIRMATION", "Awaiting confirmation"),
        NewStatus(S.CustomerPayment.Completed, G.CustomerPayment, "COMPLETED", "Completed"),
        NewStatus(S.CustomerPayment.Cancelled, G.CustomerPayment, "CANCELLED", "Cancelled", final: true),
        NewStatus(S.PurchaseRequest.AwaitingStaff, G.PurchaseRequest, "AWAITING_STAFF", "Awaiting staff confirmation"),
        NewStatus(S.CustomerPayment.PendingTransfer, G.CustomerPayment, "PENDING_TRANSFER", "Pending transfer"),
        NewStatus(S.InputIssue.Draft, G.InputIssue, "DRAFT", "Draft"),
        NewStatus(S.InputIssue.Completed, G.InputIssue, "COMPLETED", "Completed"),
        NewStatus(S.InputIssue.Cancelled, G.InputIssue, "CANCELLED", "Cancelled", final: true),
        NewStatus(S.ProductDisposal.Draft, G.ProductDisposal, "DRAFT", "Draft"),
        NewStatus(S.ProductDisposal.Completed, G.ProductDisposal, "COMPLETED", "Completed"),
        NewStatus(S.ProductDisposal.Cancelled, G.ProductDisposal, "CANCELLED", "Cancelled", final: true),
        NewStatus(S.SalesOrder.Draft, G.SalesOrder, "DRAFT", "Draft"),
        NewStatus(S.SalesOrder.Delivering, G.SalesOrder, "DELIVERING", "Delivering"),
        NewStatus(S.SalesOrder.Completed, G.SalesOrder, "COMPLETED", "Completed"),
        NewStatus(S.SalesOrder.Cancelled, G.SalesOrder, "CANCELLED", "Cancelled", final: true),
        NewStatus(S.HarvestPlan.Draft, G.HarvestPlan, "DRAFT", "Draft"),
        NewStatus(S.HarvestPlan.Assigned, G.HarvestPlan, "ASSIGNED", "Assigned"),
        NewStatus(S.HarvestPlan.Harvesting, G.HarvestPlan, "HARVESTING", "Harvesting"),
        NewStatus(S.HarvestPlan.Completed, G.HarvestPlan, "COMPLETED", "Completed"),
        NewStatus(S.HarvestPlan.Cancelled, G.HarvestPlan, "CANCELLED", "Cancelled", final: true),
        NewStatus(S.HarvestPlan.Received, G.HarvestPlan, "RECEIVED", "Đã nhận"),
        NewStatus(S.HarvestPlan.HandedOver, G.HarvestPlan, "HANDED_OVER", "Đã bàn giao"),

        // STD-NURSERY-BATCH (ươm giống, decision of 12/11/2026): a Completed batch is not cancelled.
        NewStatus(S.NurseryBatch.Nursing, G.NurseryBatch, "NURSING", "Nursing"),
        NewStatus(S.NurseryBatch.Completed, G.NurseryBatch, "COMPLETED", "Completed", final: false),
        NewStatus(S.NurseryBatch.Cancelled, G.NurseryBatch, "CANCELLED", "Cancelled", final: true),
        NewStatus(S.NurseryBatch.Closed, G.NurseryBatch, "CLOSED", "Closed", final: true),

        // STD-SALES-RETURN (BR-CLAIM-03).
        NewStatus(S.SalesReturn.Recorded, G.SalesReturn, "RECORDED", "Recorded"),
        NewStatus(S.SalesReturn.Cancelled, G.SalesReturn, "CANCELLED", "Cancelled", final: true),
    ];

    private static Status NewStatus(int id, string group, string code, string displayName, bool final = false) =>
        new()
        {
            Id = id,
            Group = group,
            Code = code,
            DisplayName = displayName,
            IsFinal = final,
            SortOrder = id % 100,
        };
}
