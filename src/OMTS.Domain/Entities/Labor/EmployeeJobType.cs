using OMTS.Domain.Entities.Identity;

namespace OMTS.Domain.Entities.Labor;

/// <summary>A job type on an employee profile: optional, it only puts the employee first when work is assigned (BR-LABOR-01).</summary>
public class EmployeeJobType
{
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public Guid JobTypeId { get; set; }
    public JobType JobType { get; set; } = null!;
}
