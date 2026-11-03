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

    public static class Cooperative
    {
        public const int Draft = 201;
        public const int Completed = 202;
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

    public static class WaterMeasurement
    {
        public const int Draft = 701;
        public const int Confirmed = 702;
        public const int Superseded = 703;
        public const int Voided = 704;
    }

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
}
