using OMTS.Domain.Common;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// One job period of an employee on a date (LABOR-02, BR-LABOR-03): the raft and the job type of the work. The first period of
/// the day is the work assigned for the date and starts at the check-in (<see cref="StartTime"/> null); each job change after
/// the check-in adds a period with its start time. The zone is the raft's zone and the cycle is the one Open on the date, so
/// neither is stored (BR-CYCLE-01, BR-LABOR-07).
/// </summary>
public class WorkAssignment : AuditableEntity
{
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    /// <summary>The business date of the work.</summary>
    public DateOnly WorkDate { get; set; }

    public Guid RaftId { get; set; }
    public Raft Raft { get; set; } = null!;

    public Guid JobTypeId { get; set; }
    public JobType JobType { get; set; } = null!;

    /// <summary>Null for the first period of the day, which starts at the check-in; the time of the job change otherwise.</summary>
    public DateTimeOffset? StartTime { get; set; }

    /// <summary>The account that assigned the work, or changed the job of the crew.</summary>
    public Guid AssignedByAccountId { get; set; }

    /// <summary>The work assigned before the check-in, which can be edited or removed until the employee is checked in.</summary>
    public bool IsFirstPeriod => StartTime is null;
}
