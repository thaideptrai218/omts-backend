using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// Money received from a customer, or paid back to one (BR-DEBT-03). It counts in the customer debt from the time it is recorded, also while Awaiting confirmation
/// (BR-DEBT-02); a payment that waits for a bank transfer (Pending transfer) counts nowhere until the transfer arrives. It may name the invoices it pays
/// (<see cref="Allocations"/>); what it does not name covers the oldest invoices. Cash recorded at the port is Awaiting confirmation until the Accountant confirms it
/// (DEBT-02). It has no zone (BR-FARM-08) and is never edited: one recorded by mistake is cancelled with a reason.
/// </summary>
public class CustomerPayment : AuditableEntity, IHasStatus
{
    /// <summary>PT000123: the "receipt" of the entry (BR-DEBT-03), one cooperative-wide sequence for both kinds; the user may type another code, unique regardless of letter case. It is the transfer content of a VietQR code.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>None for a payment without a customer: a walk-in invoice, or a transfer whose code matches no invoice (BR-DEBT-03).</summary>
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public CustomerPaymentKind Kind { get; set; }

    /// <summary>The business date (UTC+7) of <see cref="PaidAt"/>; the debt and every period filter read it.</summary>
    public DateOnly PaymentDate { get; set; }

    /// <summary>When the money moved, at UTC+7, never later than now. A payment that waits for a transfer gets it when the transfer arrives.</summary>
    public DateTimeOffset PaidAt { get; set; }

    /// <summary>Whole VND, above 0; 0 only on a payment that is all <see cref="DiscountAmount"/>. For a payment waiting for a transfer: what the customer was asked to transfer.</summary>
    public decimal Amount { get; set; }

    /// <summary>Whole VND of debt written off on this payment, with no money received; kind Payment only.</summary>
    public decimal DiscountAmount { get; set; }

    /// <summary>The bank account of the cooperative the money was received into or paid from, on a transfer only: a Bank account of the cash book.</summary>
    public Guid? CashAccountId { get; set; }
    public CashAccount? CashAccount { get; set; }

    public CustomerPaymentMethod Method { get; set; }

    /// <summary>The invoice the money came with: cash at the port, or a transfer with the VietQR code of the invoice. It covers that invoice first (BR-DEBT-05).</summary>
    public Guid? InvoiceId { get; set; }
    public SalesInvoice? Invoice { get; set; }

    /// <summary>
    /// The order this payment is an advance on. While the order is Draft or Delivering the money is reserved for that order: it lowers the customer debt but covers no invoice except the order's own. Once the order is
    /// Completed or Cancelled, what is left of it is general credit again.
    /// </summary>
    public Guid? SalesOrderId { get; set; }
    public SalesOrder? SalesOrder { get; set; }

    /// <summary>The person who collected the money ("Người thu") or paid it back; for cash at the port the Zone Manager who hands it to the Accountant (BR-SALE-07 (3)).</summary>
    public Guid? CollectedByAccountId { get; set; }

    /// <summary>The person who recorded it; null when the system did (GFR-08).</summary>
    public Guid? RecordedByAccountId { get; set; }

    /// <summary>Required for a transfer the Accountant records (BR-DEBT-03); a second payment with the same one needs a confirmation (MSG-DEBT-05).</summary>
    public string? BankReference { get; set; }

    /// <summary>
    /// A transfer reported by the Payment Notification Service (GFR-08) whose invoice code matches no saved invoice, for example after Stop sale: the code it carried, so the
    /// Accountant can find it (DEBT-02.6). Null otherwise.
    /// </summary>
    public string? TransferInvoiceCode { get; set; }

    /// <summary>The time of the transfer that the Payment Notification Service reported (GFR-08); null for a payment recorded by a person.</summary>
    public DateTimeOffset? TransferredAt { get; set; }

    public string? Note { get; set; }

    /// <summary>A repeated key returns the first payment and creates nothing (NFR-04).</summary>
    public string? DeduplicationKey { get; set; }

    public int StatusId { get; set; } = StatusIds.CustomerPayment.AwaitingConfirmation;
    public Status Status { get; set; } = null!;

    /// <summary>The invoices this payment pays, kind Payment only; set when it is recorded and trimmed only when a transfer is short.</summary>
    public ICollection<CustomerPaymentAllocation> Allocations { get; set; } = [];

    /// <summary>Who confirmed it and when: the Accountant at confirmation (DEBT-02.2), or the recorder when it is saved Completed (DEBT-02, DEBT-02.3).</summary>
    public Guid? ConfirmedByAccountId { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>A cancelled payment stays on its own date in the statement, with this date, and counts 0 (BR-DEBT-04).</summary>
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledByAccountId { get; set; }
    public string? CancelReason { get; set; }
}

/// <summary>The part of a customer payment set against one invoice of the same customer. The debt reading covers that invoice with it first.</summary>
public class CustomerPaymentAllocation : Entity
{
    public Guid PaymentId { get; set; }
    public CustomerPayment Payment { get; set; } = null!;

    public Guid InvoiceId { get; set; }
    public SalesInvoice Invoice { get; set; } = null!;

    /// <summary>Whole VND above zero.</summary>
    public decimal Amount { get; set; }
}

/// <summary>
/// A correction of the debt of a customer made by hand (adjustment): above zero the customer owes more, below zero less. It never changes: one made by mistake is
/// cancelled with a reason and then counts 0.
/// </summary>
public class CustomerDebtAdjustment : AuditableEntity
{
    /// <summary>DC000123.</summary>
    public string Code { get; set; } = string.Empty;

    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>Whole VND, never 0; signed.</summary>
    public decimal Amount { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>The business date (UTC+7) of <see cref="OccurredAt"/>.</summary>
    public DateOnly AdjustmentDate { get; set; }

    public string Reason { get; set; } = string.Empty;
    public Guid RecordedByAccountId { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledByAccountId { get; set; }
    public string? CancelReason { get; set; }
}
