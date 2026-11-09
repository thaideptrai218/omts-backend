using OMTS.Domain.Common;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// A sale whose rest is paid with the VietQR code of its invoice, held while the transfer is awaited (SALE-05 steps 7-8, BR-SALE-07 (1)). The invoice is not saved yet: the
/// code is kept for it (BR-SALE-08) and the sale as the Zone Manager confirmed it waits here, so the bank notification (GFR-08) can save the invoice even when the app is
/// closed. It counts in no figure (no debt, no invoiced kg, no lot closed). Rows are kept after the end, so a code is never given twice.
/// </summary>
public class PendingSale : AuditableEntity
{
    /// <summary>The code given to the invoice when its VietQR code was shown; the transfer content.</summary>
    public string Code { get; set; } = string.Empty;

    public PendingSaleStatus Status { get; set; } = PendingSaleStatus.Awaiting;

    /// <summary>The zone the invoice will count in (BR-SALE-08): who can see and continue the sale.</summary>
    public Guid ZoneId { get; set; }
    public FarmingZone Zone { get; set; } = null!;

    /// <summary>None for a walk-in customer.</summary>
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>The Zone Manager (or Owner) selling at the port: the creator of the invoice and the collector of its cash part.</summary>
    public Guid CreatedByAccountId { get; set; }

    /// <summary>The total of the invoice, whole VND.</summary>
    public decimal Total { get; set; }

    /// <summary>The cash part recorded with the invoice, 0 for none (SALE-05.7).</summary>
    public decimal CashAmount { get; set; }

    /// <summary>The amount of the VietQR code: the total less the cash part.</summary>
    public decimal ToPayAmount { get; set; }

    /// <summary>The request as sent and the sale as decided (lines priced, money split), as JSON: what the save of the invoice writes.</summary>
    public string SaleJson { get; set; } = string.Empty;

    /// <summary>The invoice saved from it (Completed, or PaymentChanged).</summary>
    public Guid? InvoiceId { get; set; }
    public SalesInvoice? Invoice { get; set; }

    /// <summary>A repeated key returns this sale and creates nothing.</summary>
    public string? DeduplicationKey { get; set; }

    /// <summary>When it left Awaiting, and who ended it (null: the system, on the transfer).</summary>
    public DateTimeOffset? EndedAt { get; set; }
    public Guid? EndedByAccountId { get; set; }
}
