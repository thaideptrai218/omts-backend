using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// Money received from a customer, or paid back to one (BR-DEBT-03). Payments are not allocated to invoices: they count in the customer debt from the time they are recorded, also
/// while Awaiting confirmation (BR-DEBT-02). One recorded at the port or by the system is Awaiting confirmation until the Accountant confirms it (DEBT-02). It has no zone
/// (BR-FARM-08) and is never edited: one recorded by mistake is cancelled with a reason.
/// </summary>
public class CustomerPayment : AuditableEntity, IHasStatus
{
    /// <summary>PT000123: the "receipt" of the entry (BR-DEBT-03), one cooperative-wide sequence for both kinds.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>None for a payment without a customer: a walk-in invoice, or a transfer whose code matches no invoice (BR-DEBT-03).</summary>
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public CustomerPaymentKind Kind { get; set; }

    /// <summary>Not later than today.</summary>
    public DateOnly PaymentDate { get; set; }

    /// <summary>Whole VND, above 0.</summary>
    public decimal Amount { get; set; }

    public CustomerPaymentMethod Method { get; set; }

    /// <summary>The invoice the money came with: cash at the port, or a transfer with the VietQR code of the invoice. It covers that invoice first (BR-DEBT-05).</summary>
    public Guid? InvoiceId { get; set; }
    public SalesInvoice? Invoice { get; set; }

    /// <summary>The Zone Manager who collected the cash at the port and hands it to the Accountant (BR-SALE-07 (3)).</summary>
    public Guid? CollectedByAccountId { get; set; }

    /// <summary>The person who recorded it; null when the system did (GFR-08).</summary>
    public Guid? RecordedByAccountId { get; set; }

    public int StatusId { get; set; } = StatusIds.CustomerPayment.AwaitingConfirmation;
    public Status Status { get; set; } = null!;
}
