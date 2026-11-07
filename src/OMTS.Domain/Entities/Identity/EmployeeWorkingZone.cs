using OMTS.Domain.Entities.Farming;

namespace OMTS.Domain.Entities.Identity;

/// <summary>A zone an employee usually works in (LAB-01); one row per employee and zone.</summary>
public class EmployeeWorkingZone
{
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public Guid ZoneId { get; set; }
    public FarmingZone Zone { get; set; } = null!;
}
