namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// <c>CustomerDebtAllocator</c> (BR-DEBT-05) as one SQL command, for profiles that hold no advance reserved for an open order. For each invoice that is not cancelled (rows of one profile in invoice
/// time, then code, order):
/// <list type="bullet">
/// <item>it needs its total less its sales return that stands, never below 0;</item>
/// <item>the parts of payments set against it, and the payments that came with it and name no part, cover it first, at most what it needs: that is <c>Direct</c>, and what is left is <c>Remaining</c>;</item>
/// <item>the pool is what the payments bring (money and discount) less what they covered directly, the money paid back and the debt adjustments;</item>
/// <item>a pool of 0 or more covers the remaining needs oldest first (a running sum of <c>Remaining</c> against the pool);</item>
/// <item>a pool below 0 uncovers the newest directly covered invoices for the difference (a running sum of <c>Direct</c> from the newest).</item>
/// </list>
/// Amounts are whole VND (decimal(18,0)), so every sum is exact. Codes are compared by code point, as the allocator does (ordinal).
/// The parameters: <c>@ids</c> a JSON array of the profile ids, the status ids of an invoice and a payment, the status of a sales return that stands and the two payment kinds.
/// </summary>
internal static class CustomerUnpaidInvoicesSql
{
    private const string Counted = "p.StatusId <> @paymentCancelled AND p.StatusId <> @paymentPending";

    private const string Unpaid = $"""
        SELECT w.CustomerId, w.InvoiceId, w.Code, w.InvoiceDate, w.Total, w.Ord,
            CASE
                WHEN w.Pool >= 0 THEN
                    CASE
                        WHEN w.Remaining = 0 THEN 0
                        WHEN w.CumRemaining - w.Remaining >= w.Pool THEN w.Remaining
                        WHEN w.CumRemaining <= w.Pool THEN 0
                        ELSE w.CumRemaining - w.Pool
                    END
                ELSE w.Remaining +
                    CASE
                        WHEN w.Direct = 0 THEN 0
                        WHEN w.NewerDirect >= -w.Pool THEN 0
                        WHEN w.NewerDirect + w.Direct <= -w.Pool THEN w.Direct
                        ELSE -w.Pool - w.NewerDirect
                    END
            END AS Unpaid
        FROM (
            SELECT b.CustomerId, b.InvoiceId, b.Code, b.InvoiceDate, b.Total, b.Ord, b.Remaining, b.Direct,
                b.Credit - SUM(b.Direct) OVER (PARTITION BY b.CustomerId) - b.PaidBack - b.Adjusted AS Pool,
                SUM(b.Remaining) OVER (PARTITION BY b.CustomerId ORDER BY b.Ord ROWS UNBOUNDED PRECEDING) AS CumRemaining,
                ISNULL(SUM(b.Direct) OVER (PARTITION BY b.CustomerId ORDER BY b.Ord DESC ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS NewerDirect
            FROM (
                SELECT i.CustomerId, i.Id AS InvoiceId, i.Code, i.InvoiceDate, i.Total,
                    ROW_NUMBER() OVER (PARTITION BY i.CustomerId ORDER BY i.InvoicedAt, i.Code COLLATE Latin1_General_BIN2) AS Ord,
                    n.Need - d.Direct AS Remaining, d.Direct,
                    ISNULL(pc.Credit, 0) AS Credit, ISNULL(pc.PaidBack, 0) AS PaidBack, ISNULL(ad.Adjusted, 0) AS Adjusted
                FROM SalesInvoices i
                LEFT JOIN (
                    SELECT p.CustomerId,
                        SUM(CASE WHEN p.Kind = @kindPayment THEN p.Amount + p.DiscountAmount ELSE 0 END) AS Credit,
                        SUM(CASE WHEN p.Kind = @kindPaidBack THEN p.Amount ELSE 0 END) AS PaidBack
                    FROM CustomerPayments p
                    WHERE p.CustomerId IN (SELECT CAST(j.value AS uniqueidentifier) FROM OPENJSON(@ids) j) AND {Counted}
                    GROUP BY p.CustomerId
                ) pc ON pc.CustomerId = i.CustomerId
                LEFT JOIN (
                    SELECT a.CustomerId, SUM(a.Amount) AS Adjusted
                    FROM CustomerDebtAdjustments a
                    WHERE a.CustomerId IN (SELECT CAST(j.value AS uniqueidentifier) FROM OPENJSON(@ids) j) AND a.CancelledAt IS NULL
                    GROUP BY a.CustomerId
                ) ad ON ad.CustomerId = i.CustomerId
                OUTER APPLY (
                    SELECT SUM(r.Value) AS Value FROM SalesReturns r WHERE r.InvoiceId = i.Id AND r.StatusId = @returnStands
                ) rv
                CROSS APPLY (
                    SELECT CASE WHEN i.Total - ISNULL(rv.Value, 0) > 0 THEN i.Total - ISNULL(rv.Value, 0) ELSE 0 END AS Need
                ) n
                CROSS APPLY (
                    SELECT
                        ISNULL((
                            SELECT SUM(al.Amount)
                            FROM CustomerPaymentAllocations al
                            JOIN CustomerPayments p ON p.Id = al.PaymentId
                            WHERE al.InvoiceId = i.Id AND p.CustomerId = i.CustomerId AND p.Kind = @kindPayment AND {Counted}
                        ), 0)
                        + ISNULL((
                            SELECT SUM(p.Amount + p.DiscountAmount)
                            FROM CustomerPayments p
                            WHERE p.InvoiceId = i.Id AND p.CustomerId = i.CustomerId AND p.Kind = @kindPayment AND {Counted}
                                AND NOT EXISTS (SELECT 1 FROM CustomerPaymentAllocations al WHERE al.PaymentId = p.Id)
                        ), 0) AS Eligible
                ) e
                CROSS APPLY (
                    SELECT CASE WHEN e.Eligible < n.Need THEN e.Eligible ELSE n.Need END AS Direct
                ) d
                WHERE i.CustomerId IN (SELECT CAST(j.value AS uniqueidentifier) FROM OPENJSON(@ids) j) AND i.StatusId <> @invoiceCancelled
            ) b
        ) w
        """;

    /// <summary>The rows with something unpaid: <c>CustomerId, InvoiceId, Code, InvoiceDate, Total, Unpaid, Ord</c>.</summary>
    public static readonly string All = $"""
        SELECT u.CustomerId, u.InvoiceId, u.Code, u.InvoiceDate, u.Total, u.Unpaid, u.Ord
        FROM ({Unpaid}) u
        WHERE u.Unpaid > 0
        """;

    /// <summary>The first of those rows of each profile (the oldest unpaid invoice).</summary>
    public static readonly string Oldest = $"""
        SELECT x.CustomerId, x.InvoiceId, x.Code, x.InvoiceDate, x.Total, x.Unpaid, x.Ord
        FROM (
            SELECT u.CustomerId, u.InvoiceId, u.Code, u.InvoiceDate, u.Total, u.Unpaid, u.Ord,
                ROW_NUMBER() OVER (PARTITION BY u.CustomerId ORDER BY u.Ord) AS Rn
            FROM ({Unpaid}) u
            WHERE u.Unpaid > 0
        ) x
        WHERE x.Rn = 1
        """;
}
