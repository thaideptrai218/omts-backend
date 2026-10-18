using OMTS.Domain.Common;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities;

/// <summary>
/// E45 – Money actually paid by the cooperative; seasonal wages are paid to the contractor. A wrong
/// payment is fixed by a counter-adjustment referencing the original (UC61 branch 5b).
/// </summary>
public class LaborPayment : AuditableEntity
{
    public Guid SettlementId { get; set; }
    public LaborSettlement Settlement { get; set; } = null!;

    public string Recipient { get; set; } = string.Empty;

    /// <summary>VND, multiple of 1,000; negative for a counter-adjustment.</summary>
    public decimal AmountPaid { get; set; }

    public DateOnly PaymentDate { get; set; }
    public PaymentMethod Method { get; set; }
    public string? ReferenceCode { get; set; }

    /// <summary>Receipt photo or signature.</summary>
    public List<string> DocumentUrls { get; set; } = [];

    public Guid? OriginalPaymentId { get; set; }
    public LaborPayment? OriginalPayment { get; set; }
    public string? AdjustmentReason { get; set; }
}
