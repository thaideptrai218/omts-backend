using OMTS.Domain.Common;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// E40 – Who does what work, where and when (LAB-03). The assignee is exactly one of an employee, a seasonal
/// worker or a team. Piecework is paid by the quantity the Zone Manager confirms (LAB-05); daily-wage work is
/// paid from attendance and the daily rate. Cancelled instead of deleted.
/// </summary>
public class WorkAssignment : AuditableEntity
{
    public Guid ZoneId { get; set; }
    public FarmingZone Zone { get; set; } = null!;

    public Guid? RaftId { get; set; }
    public Raft? Raft { get; set; }

    public Guid? CycleId { get; set; }
    public CultivationCycle? Cycle { get; set; }

    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public Guid? WorkerId { get; set; }
    public SeasonalWorker? Worker { get; set; }

    /// <summary>The whole team: a daily-wage assignment covers every In Use member on the work date.</summary>
    public Guid? ContractorId { get; set; }
    public Contractor? Contractor { get; set; }

    public string Task { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public DateOnly WorkDate { get; set; }
    public WorkPaymentType PaymentType { get; set; }

    /// <summary>Piecework only: unit counted (e.g. "rope", "cage").</summary>
    public string? PieceUnit { get; set; }
    public decimal? PlannedQuantity { get; set; }

    /// <summary>Piecework only: whole VND per unit, not necessarily a multiple of 1,000.</summary>
    public decimal? PieceRate { get; set; }
    public DateOnly? ExpectedCompletionDate { get; set; }

    /// <summary>Confirmed quantity (0 = nothing accepted); only confirmed piecework is paid, in the month of <see cref="CompletionDate"/>.</summary>
    public decimal? CompletedQuantity { get; set; }
    public DateOnly? CompletionDate { get; set; }
    public Guid? CompletionConfirmedByAccountId { get; set; }
    public DateTimeOffset? CompletionConfirmedAt { get; set; }
    public List<string> CompletionEvidenceUrls { get; set; } = [];
    public string? CompletionNote { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledByAccountId { get; set; }
    public string? CancellationReason { get; set; }

    public bool IsCancelled => CancelledAt is not null;

    public bool IsCompleted => CompletionConfirmedAt is not null;
}
