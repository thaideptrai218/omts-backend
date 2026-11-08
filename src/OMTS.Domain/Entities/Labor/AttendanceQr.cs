using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;
namespace OMTS.Domain.Entities.Labor;

/// <summary>LABOR-07: opaque, short-lived attendance identifier; never a login credential.</summary>
public class AttendanceQr : Entity
{
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}
