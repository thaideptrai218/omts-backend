using OMTS.Domain.Common;
using OMTS.Domain.Entities.Farming;
namespace OMTS.Domain.Entities.Labor;

/// <summary>LABOR-02/04: a job change splits the day at the server's current time.</summary>
public class AttendanceJobPeriod : Entity
{
    public Guid AttendanceId { get; set; }
    public AttendanceRecord Attendance { get; set; } = null!;
    public Guid JobTypeId { get; set; }
    public JobType JobType { get; set; } = null!;
    public Guid RaftId { get; set; }
    public Raft Raft { get; set; } = null!;
    public DateTimeOffset StartsAt { get; set; }
}
