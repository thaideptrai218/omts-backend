using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// Payroll of one calendar month (LAB-07/LAB-08): the header of the payslips. It has no status of its own; its
/// state (Draft, Sent back, Finalized, Fully paid) is derived. Finalizing locks the work, attendance and
/// piecework confirmations dated in the month.
/// </summary>
public class Payroll : AuditableEntity
{
    /// <summary>"YYYY-MM", unique.</summary>
    public string Period { get; set; } = string.Empty;
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    public Guid PreparedByAccountId { get; set; }

    /// <summary>Why the Owner sent the draft back; cleared on finalization.</summary>
    public string? ReturnReason { get; set; }
    public DateTimeOffset? ReturnedAt { get; set; }
    public Guid? ReturnedByAccountId { get; set; }

    public DateTimeOffset? FinalizedAt { get; set; }
    public Guid? FinalizedByAccountId { get; set; }

    public ICollection<LaborSettlement> Settlements { get; set; } = [];

    public bool IsFinalized => FinalizedAt is not null;
}
