using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// The sale made at the port (SALE-05, BR-SALE-06 to BR-SALE-08). It is saved only when it is paid or put on the customer debt, so there is no draft: it is Completed, or
/// Cancelled by SALE-08. Its zone is chosen by the Zone Manager and holds the whole revenue of the invoice, also of lines of lots of other zones. Whether it is paid, and what is
/// still to pay, are worked out from the payments (BR-DEBT-05), never stored.
/// </summary>
public class SalesInvoice : AuditableEntity, IHasStatus
{
    /// <summary>HD000123 (BR-SALE-08): one cooperative-wide sequence, never given twice.</summary>
    public string Code { get; set; } = string.Empty;

    public DateTimeOffset InvoicedAt { get; set; }

    /// <summary>The business date (UTC+7) of <see cref="InvoicedAt"/>: what the searches, statements and reports filter on.</summary>
    public DateOnly InvoiceDate { get; set; }

    public Guid ZoneId { get; set; }
    public FarmingZone Zone { get; set; } = null!;

    /// <summary>None for a walk-in customer (BR-SALE-07): the invoice and its payments then count in no customer debt.</summary>
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public Guid? PurchaseRequestId { get; set; }
    public PurchaseRequest? PurchaseRequest { get; set; }

    /// <summary>The request is Fulfilled while a Completed invoice linked to it keeps this mark (BR-SALE-01).</summary>
    public bool TakesRequestInFull { get; set; }

    /// <summary>Whole VND, 0 or more; kept only when the Cooperative Owner decided it (BR-SALE-06).</summary>
    public decimal DiscountAmount { get; set; }
    public bool DiscountDecidedByOwner { get; set; }

    /// <summary>Whole VND, 0 or more; needs no decision of the Cooperative Owner.</summary>
    public decimal OtherCharges { get; set; }

    /// <summary>The sum of the line amounts.</summary>
    public decimal Subtotal { get; set; }

    /// <summary>The sum of the price cuts (SALE-07), 0 until one is entered.</summary>
    public decimal PriceCutTotal { get; set; }

    /// <summary>Subtotal − discount + other charges − price cuts, never below 0 (BR-SALE-06); kept at its latest values.</summary>
    public decimal Total { get; set; }

    /// <summary>Set at the save and never changed by a correction (BR-SALE-10).</summary>
    public SalesPaymentTerm PaymentTerm { get; set; }

    public Guid CreatedByAccountId { get; set; }

    /// <summary>A repeated key returns the first invoice and creates nothing: a second tap on Save sells nothing twice.</summary>
    public string? DeduplicationKey { get; set; }

    public int StatusId { get; set; } = StatusIds.SalesInvoice.Completed;
    public Status Status { get; set; } = null!;

    /// <summary>
    /// The QR code of the invoice (BR-TRACE-02, BR-ORIGIN-01): 128 random bits, base64url, that cannot be guessed. Created at the end of the sale only when the trader wanted it
    /// and at least one lot of the invoice was Published; never created later, kept on a correction and after a cancellation.
    /// </summary>
    public string? QrToken { get; set; }
    public DateTimeOffset? QrCreatedAt { get; set; }

    /// <summary>When the end of the sale answered the QR question (SALE-05 step 9) and sent the invoice the first time: it happens once, with or without a QR code.</summary>
    public DateTimeOffset? QrDecidedAt { get; set; }

    /// <summary>The kind, time and reason of the latest correction (SALE-07 POST-1); each correction is kept as one event in the audit log (BR-SALE-10).</summary>
    public SalesInvoiceCorrectionKind? LastCorrectionKind { get; set; }
    public DateTimeOffset? LastCorrectedAt { get; set; }
    public string? LastCorrectionReason { get; set; }

    /// <summary>Set by SALE-08: a Cancelled invoice keeps its cancel date and reason (BR-SALE-11).</summary>
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledByAccountId { get; set; }
    public string? CancelReason { get; set; }

    public ICollection<SalesInvoiceLine> Lines { get; set; } = [];
    public ICollection<SalesInvoicePriceCut> PriceCuts { get; set; } = [];
    public ICollection<CustomerPayment> Payments { get; set; } = [];
}

/// <summary>One product sold from one harvest lot, with the kg weighed at the port (BR-SALE-06). It links the sale to the lot for the yield figures and the forward trace.</summary>
public class SalesInvoiceLine : Entity
{
    public Guid InvoiceId { get; set; }
    public SalesInvoice Invoice { get; set; } = null!;

    /// <summary>1, 2, … in the order entered.</summary>
    public int LineNumber { get; set; }

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public Guid HarvestLotId { get; set; }
    public HarvestLot HarvestLot { get; set; } = null!;

    /// <summary>Above 0, at most 3 decimals.</summary>
    public decimal Kg { get; set; }

    /// <summary>Whole VND per kg, above 0: the list price, or the price the Cooperative Owner decided.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>The list price of the product when the line was saved: a later price change does not touch the invoice (BR-SALE-05), and MSG-SALE-08 shows it.</summary>
    public decimal ListPrice { get; set; }

    public SalesPriceSource PriceSource { get; set; }

    /// <summary>Kg × unit price, rounded half up to the dong (PM default of the plan; BR-SALE-06 says "not rounded", which means no rounding to thousands).</summary>
    public decimal Amount { get; set; }

    /// <summary>The "sold the whole lot" tick: one value for every line of the same lot. A ticked lot is closed when the invoice is saved (BR-HARVEST-04).</summary>
    public bool SoldWholeLot { get; set; }
}

/// <summary>A price cut of the whole invoice after an accepted quality claim (BR-SALE-10 (3)), entered by the Cooperative Owner in SALE-07; several add up. The lines keep their prices.</summary>
public class SalesInvoicePriceCut : Entity
{
    public Guid InvoiceId { get; set; }
    public SalesInvoice Invoice { get; set; } = null!;

    /// <summary>Whole VND, above 0.</summary>
    public decimal Amount { get; set; }

    public DateOnly CutDate { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid EnteredByAccountId { get; set; }
}
