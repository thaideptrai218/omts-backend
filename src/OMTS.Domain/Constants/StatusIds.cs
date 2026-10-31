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

    public static class WaterMeasurement
    {
        public const int Draft = 701;
        public const int Confirmed = 702;
        public const int Superseded = 703;
        public const int Voided = 704;
    }

    public static class SalesOrder
    {
        public const int PendingReview = 801;
        public const int AwaitingCustomer = 802;
        public const int Confirmed = 803;
        public const int Harvesting = 804;
        public const int Harvested = 805;
        public const int HandedOver = 806;
        public const int Sold = 807;
        public const int ReturnRequested = 808;
        public const int ReturnAccepted = 809;
        public const int Returned = 810;
        public const int Rejected = 811;
        public const int Cancelled = 812;
    }

    public static class HarvestEvent
    {
        public const int Draft = 901;
        public const int Confirmed = 902;
    }

    public static class TracePublication
    {
        public const int Draft = 1001;
        public const int Passed = 1002;
        public const int Published = 1003;
        public const int NeedsReview = 1004;
        public const int Superseded = 1005;
        public const int Withdrawn = 1006;
    }

    public static class QualityClaim
    {
        public const int Pending = 1101;
        public const int InProgress = 1102;
        public const int Closed = 1103;
    }

    public static class PhysicalReturn
    {
        public const int Holding = 1201;
        public const int Assessed = 1202;
        public const int Disposed = 1203;
    }

    public static class SaleAdjustment
    {
        public const int Draft = 1301;
        public const int Approved = 1302;
        public const int Rejected = 1303;
    }

    public static class PayRule
    {
        public const int Draft = 1401;
        public const int Approved = 1402;
        public const int Rejected = 1403;
    }

    public static class LaborSettlement
    {
        public const int Draft = 1501;
        public const int Approved = 1502;
        public const int Paid = 1503;
    }

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

    public static class MessageDelivery
    {
        public const int Sending = 1601;
        public const int Sent = 1602;
        public const int NotSent = 1603;
    }
}
