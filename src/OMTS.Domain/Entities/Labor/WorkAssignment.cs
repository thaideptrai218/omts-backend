using OMTS.Domain.Common;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// One job period of an employee: a job type on a raft on a date (LABOR-02, BR-LABOR-03). The first period of a day is the work
/// assigned for it and starts at the check-in (<see cref="StartsAt"/> is null); each change of job during the day adds one that
/// starts at the time of the change. A period lasts until the next one or the check-out, which gives the hours of each job type
/// and the labour cost of each raft (BR-LABOR-05, BR-LABOR-07).
/// </summary>
public class WorkAssignment : AuditableEntity
{
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    /// <summary>Business date (UTC+7). Work is assigned for today or a later date.</summary>
    public DateOnly Date { get; set; }

    public Guid RaftId { get; set; }
    public Raft Raft { get; set; } = null!;

    public Guid JobTypeId { get; set; }
    public JobType JobType { get; set; } = null!;

    /// <summary>Empty for the first period of the day.</summary>
    public DateTimeOffset? StartsAt { get; set; }

    public Guid AssignedByAccountId { get; set; }

    /// <summary><see cref="AttendanceMethod.Correction"/> when the Cooperative Owner corrected this period (LABOR-04.4); otherwise null.</summary>
    public AttendanceMethod? Method { get; set; }

    public string? CorrectionReason { get; set; }
}
