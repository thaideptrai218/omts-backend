using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// E39 – Seasonal worker or crew leader without a login account (LAB-02). Duplicates are identified by full
/// name + phone number, or by citizen ID. Cooperative-wide: not tied to a zone.
/// </summary>
public class SeasonalWorker : AuditableEntity, IHasStatus
{
    /// <summary>Set when the same person is also an employee.</summary>
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public string FullName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? CitizenId { get; set; }

    /// <summary>Current team; attendance keeps the team of each day, so changing it affects only future rows.</summary>
    public Guid? ContractorId { get; set; }
    public Contractor? Contractor { get; set; }

    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;
}
