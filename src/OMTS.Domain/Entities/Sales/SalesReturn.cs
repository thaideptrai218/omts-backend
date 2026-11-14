using System.Linq.Expressions;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// All the goods of one sales invoice that the trader sent back (CLAIM-03, BR-CLAIM-03), with or without a quality claim. Dated the day it is recorded and never edited: the Cooperative
/// Owner cancels one recorded by mistake, with a reason, and it then counts nowhere. An invoice has at most one that is not cancelled, and it locks the invoice.
/// </summary>
public class SalesReturn : AuditableEntity, IHasStatus
{
    /// <summary>The one rule of "has a sales return" (BR-CLAIM-03: a return that is not cancelled), so queries and rows loaded in memory cannot disagree.</summary>
    public static readonly Expression<Func<SalesReturn, bool>> Standing = r => r.StatusId == StatusIds.SalesReturn.Recorded;

    private static readonly Func<SalesReturn, bool> StandingNow = Standing.Compile();

    public bool IsStanding => StandingNow(this);

    /// <summary>TL000123: one cooperative-wide sequence, never given twice.</summary>
    public string Code { get; set; } = string.Empty;

    public Guid InvoiceId { get; set; }
    public SalesInvoice Invoice { get; set; } = null!;

    public DateOnly ReturnDate { get; set; }
    public string Reason { get; set; } = string.Empty;

    /// <summary>The total of the invoice at its latest values when the return was recorded (BR-CLAIM-03), whole VND. The invoice is locked while the return stands, so this is its total.</summary>
    public decimal Value { get; set; }

    public Guid RecordedByAccountId { get; set; }

    public int StatusId { get; set; } = StatusIds.SalesReturn.Recorded;
    public Status Status { get; set; } = null!;

    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledByAccountId { get; set; }
    public string? CancelReason { get; set; }

    public ICollection<SalesReturnLine> Lines { get; set; } = [];
}

/// <summary>
/// The return of one invoice line: its kg and whether the oysters go back for sale or are thrown away. <see cref="InvoiceLineId"/> is no foreign key: once the return is cancelled, a correction
/// (SALE-07) may remove the line, and the return keeps its record of it. While the return stands the invoice is locked, so the line is there. The lot is kept here for the same reason.
/// </summary>
public class SalesReturnLine : Entity
{
    public Guid ReturnId { get; set; }
    public SalesReturn Return { get; set; } = null!;

    public Guid InvoiceLineId { get; set; }

    public Guid HarvestLotId { get; set; }
    public HarvestLot HarvestLot { get; set; } = null!;

    /// <summary>All the kg of the invoice line: there is no partial return (BF-05.1).</summary>
    public decimal ReturnedKg { get; set; }

    public SalesReturnOutcome Outcome { get; set; }

    /// <summary>The returned kg when thrown away, else 0 (BR-HARVEST-07).</summary>
    public decimal DiscardedKg { get; set; }
}
