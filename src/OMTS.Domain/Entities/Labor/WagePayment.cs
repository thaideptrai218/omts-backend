using OMTS.Domain.Common;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// Money paid to an employee (LABOR-14, BR-LABOR-11): a Wage payment pays one payslip of a Finalized payroll, an Advance pays nothing
/// yet and is deducted from the next payslip finalized for the employee. Each payment is one cash voucher of the cash book, which
/// holds its code, its date and whether it was cancelled; a payment whose voucher is cancelled no longer counts as paid or advanced.
/// </summary>
public class WagePayment : AuditableEntity
{
    public WagePaymentKind Kind { get; set; }

    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    /// <summary>The payslip paid; null for an Advance.</summary>
    public Guid? PayrollLineId { get; set; }
    public PayrollLine? PayrollLine { get; set; }

    public Guid CashVoucherId { get; set; }
    public CashVoucher CashVoucher { get; set; } = null!;

    /// <summary>Whole VND above zero; also on the voucher.</summary>
    public decimal Amount { get; set; }

    public DateTimeOffset PaidAt { get; set; }
    public PaymentMethod Method { get; set; }
    public string? Note { get; set; }
    public Guid RecordedByAccountId { get; set; }

    /// <summary>The key a client sent with the request (Idempotency-Key header) and the position of this payment in it: a repeated request creates nothing.</summary>
    public string? RequestKey { get; set; }
    public int RequestSequence { get; set; }

    public ICollection<PayrollAdvanceDeduction> Deductions { get; set; } = [];
}

/// <summary>The part of an advance a payslip took off when its payroll was finalized.</summary>
public class PayrollAdvanceDeduction : Entity
{
    public Guid AdvanceId { get; set; }
    public WagePayment Advance { get; set; } = null!;

    public Guid PayrollLineId { get; set; }
    public PayrollLine PayrollLine { get; set; } = null!;

    public decimal Amount { get; set; }
}
