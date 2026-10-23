using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// E45 – Money actually paid by the cooperative (LAB-09): a payment of a finalized payslip, or a salary advance
/// to an employee or seasonal worker (no payslip) that a later payroll deducts. Payments are never deleted: a
/// wrong one is reversed by a negative payment referencing the original.
/// </summary>
public class LaborPayment : AuditableEntity
{
    /// <summary>The payslip paid; empty for an advance.</summary>
    public Guid? SettlementId { get; set; }
    public LaborSettlement? Settlement { get; set; }

    /// <summary>Advance only: who received it (exactly one of EmployeeId/WorkerId).</summary>
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public Guid? WorkerId { get; set; }
    public SeasonalWorker? Worker { get; set; }

    public bool IsAdvance { get; set; }

    /// <summary>Advance only: the payslip of the finalized payroll that deducted it in full.</summary>
    public Guid? DeductedInSettlementId { get; set; }
    public LaborSettlement? DeductedInSettlement { get; set; }

    public string Recipient { get; set; } = string.Empty;

    /// <summary>VND, multiple of 1,000; negative for a reversal.</summary>
    public decimal AmountPaid { get; set; }

    public DateOnly PaymentDate { get; set; }
    public PaymentMethod Method { get; set; }
    public string? ReferenceCode { get; set; }
    public string? Notes { get; set; }

    /// <summary>Receipt photo or signature.</summary>
    public List<string> DocumentUrls { get; set; } = [];

    /// <summary>Reversal only: the payment it cancels, and why.</summary>
    public Guid? OriginalPaymentId { get; set; }
    public LaborPayment? OriginalPayment { get; set; }
    public string? AdjustmentReason { get; set; }
}
