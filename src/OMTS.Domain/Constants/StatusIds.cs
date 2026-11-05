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
    }

    /// <summary>LANE (BR-FARM-04): a lane taken off the end of a raft is Removed, never deleted, and may return to In Use.</summary>
    public static class Lane
    {
        public const int InUse = 2001;
        public const int Removed = 2002;
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
    }
}
