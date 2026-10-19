using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// E39 – Seasonal worker identity for daily wages; needs no account. Duplicates are identified by
/// full name + phone number, or by citizen ID when present.
/// </summary>
public class SeasonalWorker : AuditableEntity, IHasStatus
{
    /// <summary>Set when the same person is also an employee.</summary>
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public string FullName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? CitizenId { get; set; }

    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;
}
