namespace OMTS.Domain.Constants;

/// <summary>
/// Fixed primary keys of the seeded Status catalog (Table 3.4-1 and 3.4.3). Status codes are fixed by
/// the SRS (BR-29), so the ids are stable and safe to reference from code. Ids are grouped by hundreds.
/// </summary>
public static class StatusIds
{

    public static class Usage
    {
        public const int InUse = 101;
        public const int Discontinued = 102;
    }

    public static class Account
    {
        public const int Active = 301;
        public const int Locked = 302;

        /// <summary>Trader who asked for an account from the Zalo Mini App; cannot log in until approved.</summary>
        public const int PendingApproval = 303;

        /// <summary>The Cooperative Owner refused the request; the person may ask again.</summary>
        public const int Rejected = 304;
    }

    public static class Employee
    {
        public const int Working = 401;
        public const int Left = 402;
    }

    public static class AccessGrant
    {
        public const int Effective = 501;
        public const int Revoked = 502;
    }

    public static class CultivationCycle
    {
        public const int Open = 601;
        public const int Closed = 602;
    }

    // 701–704 were the Draft/Confirmed/Corrected/Voided water statuses of the SRS 22/10 model (removed in OMTS-219).
    // 801–1603 belonged to the SRS 22/10 order, harvest, traceability, claim and labor model (removed in
    // OMTS-216); new groups take fresh hundreds so a removed id is never reused with another meaning.

    public static class PurchaseReceipt
    {
        public const int Draft = 1701;
        public const int Completed = 1702;
        public const int Cancelled = 1703;
    }

    public static class SupplierPayment
    {
        public const int Recorded = 1801;
        public const int Cancelled = 1802;
    }

    public static class Payroll
    {
        public const int Draft = 3401;
        public const int AwaitingFinalization = 3402;
        public const int SentBack = 3403;
        public const int Finalized = 3404;

        /// <summary>The web contract's "Creating": a sheet being made. Sheets are made in one step, so a stored one is never in it.</summary>
        public const int Creating = 3405;

        /// <summary>The web contract's "Cancelled": a sheet dropped before it was finalized; it no longer counts anywhere.</summary>
        public const int Cancelled = 3406;

        /// <summary>The web contract's "Provisional" is the catalog's Draft: lines follow the current records and nothing is locked.</summary>
        public const int Provisional = Draft;
    }

    /// <summary>STD-EMPLOYEE-REQUEST (BR-LABOR-08): a reviewed or cancelled request does not change any more.</summary>
    public static class EmployeeRequest
    {
        public const int AwaitingReview = 3801;
        public const int Approved = 3802;
        public const int Rejected = 3803;
        public const int Cancelled = 3804;
    }

    public static class PurchaseReturn
    {
        public const int Recorded = 1901;
        public const int Cancelled = 1902;

        /// <summary>Saved but not completed: holds no quantity, lowers no debt and is removed rather than cancelled.</summary>
        public const int Draft = 1903;
    }

    /// <summary>LANE (BR-FARM-04): a lane taken off the end of a raft is Removed, never deleted, and may return to In Use.</summary>
    public static class Lane
    {
        public const int InUse = 2001;
        public const int Removed = 2002;
    }

    /// <summary>HARVEST_LOT (BR-HARVEST-04): Open and Reopened lots are for sale; a Closed lot can be reopened by the system.</summary>
    public static class HarvestLot
    {
        public const int Open = 2901;
        public const int Reopened = 2902;
        public const int Closed = 2903;
    }

    public static class MessageDelivery
    {
        public const int Sending = 1601;
        public const int Sent = 1602;
        public const int NotSent = 1603;
    }

    public static class WaterMeasurement
    {
        public const int Active = 2101;
        public const int Voided = 2102;
    }

    /// <summary>SALE-01 to SALE-03; Fulfilled is worked out from the invoices, not stored (BR-SALE-01).</summary>
    public static class PurchaseRequest
    {
        public const int AwaitingResponse = 3201;
        public const int Accepted = 3202;
        public const int Declined = 3203;
        public const int Cancelled = 3204;
        public const int AwaitingStaff = 3205;
    }

    /// <summary>STD-QUALITY-CLAIM (BR-CLAIM-02): a response cannot be changed.</summary>
    public static class QualityClaim
    {
        public const int AwaitingResponse = 3701;
        public const int Rejected = 3702;
        public const int Accepted = 3703;
    }

    /// <summary>STD-SALES-INVOICE (BR-SALE-08): saved only when paid or put on the debt, so no draft; Paid and the delivery status are worked out.</summary>
    public static class SalesInvoice
    {
        public const int Completed = 3501;
        public const int Cancelled = 3502;
    }

    /// <summary>STD-CUSTOMER-PAYMENT (BR-DEBT-03): counts in the debt from its recording, also while Awaiting confirmation; Pending transfer counts nowhere until the transfer arrives.</summary>
    public static class CustomerPayment
    {
        public const int AwaitingConfirmation = 3601;
        public const int Completed = 3602;
        public const int Cancelled = 3603;
        public const int PendingTransfer = 3604;
    }

    /// <summary>An input issue: a Draft holds no quantity and is removed; Completed writes the movements; Cancelled counts nowhere.</summary>
    public static class InputIssue
    {
        public const int Draft = 3901;
        public const int Completed = 3902;
        public const int Cancelled = 3903;
    }
}
