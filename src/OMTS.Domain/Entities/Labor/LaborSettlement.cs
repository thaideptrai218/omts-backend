using OMTS.Domain.Common;
using OMTS.Domain.Statuses;

namespace OMTS.Domain.Entities;

/// <summary>
/// E44 – Amount payable per person/contractor and period; finalizing is not paying. Becomes Fully Paid
/// when total payments equal the approved obligation.
/// </summary>
public class LaborSettlement : AuditableEntity, IHasStatus
{
    public Guid? ContractorId { get; set; }
    public Contractor? Contractor { get; set; }

    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    /// <summary>VND, multiple of 1,000.</summary>
    public decimal TotalObligation { get; set; }

    public Guid PreparedByAccountId { get; set; }
    public Guid? ApprovedByAccountId { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }

    public int StatusId { get; set; } = StatusIds.LaborSettlement.Draft;
    public Status Status { get; set; } = null!;

    public ICollection<PayLine> PayLines { get; set; } = [];
    public ICollection<LaborPayment> Payments { get; set; } = [];
}
