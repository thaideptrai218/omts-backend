using OMTS.Domain.Common;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// E40 – Who does what work, where and when. Daily wage: exactly one of EmployeeId/WorkerId.
/// Task contract: a contractor and contract amount. Kept only because ending an employment checks later
/// assignments (LAB-01); the SRS 27/10 labor module replaces it.
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

    public Guid? ContractorId { get; set; }
    public Contractor? Contractor { get; set; }

    public string Task { get; set; } = string.Empty;
    public DateOnly WorkDate { get; set; }
    public WorkPaymentType PaymentType { get; set; }

    /// <summary>Task contract only; VND, multiple of 1,000.</summary>
    public decimal? ContractAmount { get; set; }
    public DateOnly? ExpectedCompletionDate { get; set; }

    public DateOnly? AcceptanceDate { get; set; }
    public bool? AcceptancePassed { get; set; }
    public List<string> AcceptanceEvidenceUrls { get; set; } = [];

    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledByAccountId { get; set; }
    public string? CancellationReason { get; set; }
}
